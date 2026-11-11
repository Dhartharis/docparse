using System.Security.Cryptography;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.OperationalData.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class PushInbox(IConfiguration configuration, IDataProtectionProvider protection)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct); return db;
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params (string Name, object Value)[] values)
    {
        var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    public async Task<VerifiedPushKey?> ResolveAsync(Guid id, string key, CancellationToken ct)
    {
        OperationalSource source;
        Guid credential;
        string encrypted;
        await using (var db = await OpenAsync(ct))
        await using (var command = Command(db, """
            SELECT c.organization_id,c.revision,c.source_business_id,c.price_max_age_seconds,c.stock_max_age_seconds,c.clock_skew_seconds,
                k.id,k.protected_secret FROM integrations.connections c
            JOIN integrations.credentials k ON k.organization_id=c.organization_id AND k.connection_id=c.id
            WHERE c.id=@id AND c.disconnected_at IS NULL AND c.push AND k.direction='push'
                AND k.key_id=@key AND k.revoked_at IS NULL AND k.expires_at>now()
            """, ("id", id), ("key", key)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            source = new(id, reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), true);
            credential = reader.GetGuid(6); encrypted = reader.GetString(7);
        }
        try
        {
            var secret = protection.CreateProtector("DocParse.Integrations.Credential.v1", source.OrganizationId.ToString(), id.ToString(), "push", key).Unprotect(encrypted);
            return new(source, credential, key, secret);
        }
        catch (CryptographicException) { return null; }
    }

    private static async Task AdmitAsync(NpgsqlConnection db, VerifiedPushKey key, OperationalSignature signature, CancellationToken ct)
    {
        // Fence configuration/key revocation after signature verification and before durable acceptance.
        await using (var active = Command(db, """
            SELECT c.id FROM integrations.connections c WHERE c.id=@id AND c.organization_id=@org AND c.revision=@revision
                AND c.disconnected_at IS NULL AND c.push AND EXISTS(SELECT 1 FROM integrations.credentials k
                    WHERE k.connection_id=c.id AND k.id=@credential AND k.revoked_at IS NULL AND k.expires_at>now()) FOR UPDATE
            """, ("id", key.Source.Id), ("org", key.Source.OrganizationId), ("revision", key.Source.Revision), ("credential", key.CredentialId)))
            if (await active.ExecuteScalarAsync(ct) is null) throw new PushRejected(401, "invalid_credentials");
        await using (var cleanup = Command(db, "DELETE FROM integrations.request_nonces WHERE connection_id=@id AND expires_at<now()", ("id", key.Source.Id)))
            await cleanup.ExecuteNonQueryAsync(ct);
        await using (var nonce = Command(db, "INSERT INTO integrations.request_nonces(connection_id,request_id,expires_at) VALUES(@id,@request,@expires) ON CONFLICT DO NOTHING RETURNING request_id",
            ("id", key.Source.Id), ("request", signature.RequestId), ("expires", signature.NonceExpiry)))
            if (await nonce.ExecuteScalarAsync(ct) is null) throw new PushRejected(409, "request_replayed");
        await using (var window = Command(db, """
            INSERT INTO integrations.push_windows(connection_id,window_start,count) VALUES(@id,date_trunc('minute',now()),1)
            ON CONFLICT(connection_id,window_start) DO UPDATE SET count=push_windows.count+1 WHERE push_windows.count<120 RETURNING count
            """, ("id", key.Source.Id)))
            if (await window.ExecuteScalarAsync(ct) is null) throw new PushRejected(429, "rate_limited");
        await using var old = Command(db, "DELETE FROM integrations.push_windows WHERE connection_id=@id AND window_start<now()-interval '1 hour'", ("id", key.Source.Id));
        await old.ExecuteNonQueryAsync(ct);
    }

    private static PushReceipt Receipt(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4));

    public async Task<(PushReceipt Receipt, bool Duplicate)> AcceptAsync(VerifiedPushKey key, OperationalSignature signature, PushEvent message, byte[] hash, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await AdmitAsync(db, key, signature, ct);
        PushReceipt? existing = null;
        await using (var duplicate = Command(db, """
            SELECT id,event_id,state,received_at,error_code,body_hash FROM integrations.push_inbox WHERE connection_id=@connection AND event_id=@event
            """, ("connection", key.Source.Id), ("event", message.EventId)))
        await using (var reader = await duplicate.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, reader.GetFieldValue<byte[]>(5))) throw new PushRejected(409, "event_conflict");
                existing = Receipt(reader);
            }
        }
        if (existing is not null) { await tx.CommitAsync(ct); return (existing, true); }
        PushReceipt receipt;
        await using (var insert = Command(db, """
            INSERT INTO integrations.push_inbox(id,organization_id,connection_id,event_id,body_hash,payload,state)
            VALUES(@id,@org,@connection,@event,@hash,@payload::jsonb,'accepted') RETURNING id,event_id,state,received_at,error_code
            """, ("id", Guid.NewGuid()), ("org", key.Source.OrganizationId), ("connection", key.Source.Id), ("event", message.EventId), ("hash", hash), ("payload", message.CanonicalPayload)))
        await using (var reader = await insert.ExecuteReaderAsync(ct)) { await reader.ReadAsync(ct); receipt = Receipt(reader); }
        await tx.CommitAsync(ct);
        return (receipt, false);
    }

    public async Task<PushReceipt?> GetReceiptAsync(VerifiedPushKey key, OperationalSignature signature, Guid receiptId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await AdmitAsync(db, key, signature, ct);
        PushReceipt? result;
        await using (var query = Command(db, "SELECT id,event_id,state,received_at,error_code FROM integrations.push_inbox WHERE id=@receipt AND organization_id=@org AND connection_id=@connection",
            ("receipt", receiptId), ("org", key.Source.OrganizationId), ("connection", key.Source.Id)))
        await using (var reader = await query.ExecuteReaderAsync(ct)) { result = await reader.ReadAsync(ct) ? Receipt(reader) : null; }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PushWork?> ClaimAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var exhausted = Command(db, "UPDATE integrations.push_inbox SET state='failed',error_code='processing_failed',lease_id=NULL,lease_until=NULL,updated_at=now() WHERE state='processing' AND lease_until<=now() AND attempts>=5"))
            await exhausted.ExecuteNonQueryAsync(ct);
        var lease = Guid.NewGuid();
        Guid id = default, org = default, connection = default;
        int attempts = 0;
        string payload = "";
        var found = false;
        await using (var claim = Command(db, """
            UPDATE integrations.push_inbox SET state='processing',lease_id=@lease,lease_until=now()+interval '2 minutes',attempts=attempts+1,updated_at=now()
            WHERE id=(SELECT id FROM integrations.push_inbox WHERE attempts<5 AND ((state='accepted' AND due_at<=now())
                OR (state='processing' AND lease_until<=now())) ORDER BY received_at,id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id,organization_id,connection_id,attempts,payload::text
            """, ("lease", lease)))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                found = true;
                id = reader.GetGuid(0); org = reader.GetGuid(1); connection = reader.GetGuid(2); attempts = reader.GetInt32(3); payload = reader.GetString(4);
            }
        }
        if (!found) { await tx.CommitAsync(ct); return null; }
        int? skew;
        await using (var scope = Command(db, "SELECT clock_skew_seconds FROM integrations.connections WHERE organization_id=@org AND id=@connection AND push AND disconnected_at IS NULL", ("org", org), ("connection", connection)))
            skew = await scope.ExecuteScalarAsync(ct) is int value ? value : null;
        if (skew is null)
        {
            await using var stop = Command(db, "UPDATE integrations.push_inbox SET state='failed',error_code='processing_failed',lease_id=NULL,lease_until=NULL WHERE id=@id", ("id", id));
            await stop.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return null;
        }
        await tx.CommitAsync(ct);
        return new(id, org, connection, lease, attempts, payload, skew.Value);
    }

    public async Task FinishAsync(PushWork work, string state, string? error, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            UPDATE integrations.push_inbox SET state=@state,error_code=NULLIF(@error,''),lease_id=NULL,lease_until=NULL,updated_at=now()
            WHERE id=@id AND lease_id=@lease AND state='processing' AND lease_until>now()
            """, ("state", state), ("error", error ?? ""), ("id", work.ReceiptId), ("lease", work.LeaseId));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task ApplyAsync(PushWork work, OperationalChange change, IOperationalProjection projection, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using(var source=Command(db,"SELECT id FROM integrations.connections WHERE organization_id=@org AND id=@connection FOR UPDATE",("org",work.OrganizationId),("connection",work.ConnectionId)))
            if(await source.ExecuteScalarAsync(ct)is null)return;
        await using (var fence = Command(db, "SELECT id FROM integrations.push_inbox WHERE id=@id AND lease_id=@lease AND state='processing' AND lease_until>clock_timestamp() FOR UPDATE",
            ("id", work.ReceiptId), ("lease", work.LeaseId)))
            if (await fence.ExecuteScalarAsync(ct) is null) return;
        var outcome = await projection.ApplyAsync(work.OrganizationId, work.ConnectionId, change, now, work.ClockSkewSeconds, tx, ct);
        var state = outcome switch { ProjectionOutcome.VersionConflict => "failed", ProjectionOutcome.IgnoredOlderVersion => "ignored_older_version", _ => "applied" };
        await using (var finish = Command(db, """
            UPDATE integrations.push_inbox SET state=@state,error_code=NULLIF(@error,''),lease_id=NULL,lease_until=NULL,updated_at=now()
            WHERE id=@id AND lease_id=@lease AND state='processing' AND lease_until>clock_timestamp()
            """, ("state", state), ("error", outcome == ProjectionOutcome.VersionConflict ? "record_version_conflict" : ""), ("id", work.ReceiptId), ("lease", work.LeaseId)))
            if (await finish.ExecuteNonQueryAsync(ct) != 1) return; // Roll back the projection if the lease expired during local processing.
        await tx.CommitAsync(ct);
    }
    public async Task RetryAsync(PushWork work, CancellationToken ct)
    {
        if (work.Attempts >= 5) { await FinishAsync(work, "failed", "processing_failed", ct); return; }
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            UPDATE integrations.push_inbox SET state='accepted',due_at=now()+make_interval(secs=>@delay),lease_id=NULL,lease_until=NULL,updated_at=now()
            WHERE id=@id AND lease_id=@lease AND state='processing' AND lease_until>now()
            """, ("delay", new[] { 10, 30, 120, 300 }[work.Attempts - 1]), ("id", work.ReceiptId), ("lease", work.LeaseId));
        await command.ExecuteNonQueryAsync(ct);
    }
}
