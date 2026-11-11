using System.Security.Cryptography;
using System.Text.Json;
using DocParse.Modules.Integrations.Application;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;
using ConnectionInfo = DocParse.Modules.Integrations.Application.ConnectionInfo;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class ConnectionStore(IConfiguration configuration, IDataProtectionProvider protection, TimeProvider clock) : IConnectionStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct);
        return db;
    }

    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in values)
            command.Parameters.Add(value is null ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value } : new NpgsqlParameter(name, value));
        return command;
    }

    private async Task<ConnectionInfo[]> ReadAsync(NpgsqlConnection db, Guid org, Guid? id, CancellationToken ct)
    {
        var result = new List<ConnectionInfo>();
        const string query = """
            SELECT c.id,c.source_business_id,c.base_url,c.push,c.pull_live,c.pull_scheduled,
                c.name,c.price_max_age_seconds,c.stock_max_age_seconds,c.clock_skew_seconds,c.sync_interval_seconds,c.revision,c.disconnected_at,
                p.key_id,p.expires_at,r.key_id
            FROM integrations.connections c
            LEFT JOIN integrations.credentials p ON p.organization_id=c.organization_id AND p.connection_id=c.id AND p.direction='push' AND p.revoked_at IS NULL
            LEFT JOIN integrations.credentials r ON r.organization_id=c.organization_id AND r.connection_id=c.id AND r.direction='pull' AND r.revoked_at IS NULL
            WHERE c.organization_id=@org
            """;
        await using var command = Command(db, query + (id is null ? " ORDER BY c.created_at DESC,c.id" : " AND c.id=@id"), ("org", org));
        if (id is not null) command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var push = reader.GetBoolean(3);
            var live = reader.GetBoolean(4);
            var scheduled = reader.GetBoolean(5);
            DateTimeOffset? disconnected = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12);
            PushKeyInfo? pushKey = reader.IsDBNull(13) ? null : new(reader.GetString(13), reader.GetFieldValue<DateTimeOffset>(14));
            var pullKey = reader.IsDBNull(15) ? null : reader.GetString(15);
            var ready = (!push || (pushKey is not null && pushKey.ExpiresAt > clock.GetUtcNow())) && (!(live || scheduled) || pullKey is not null);
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), push, live, scheduled,
                new(reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10)),
                reader.GetGuid(11), disconnected, pushKey, pullKey, disconnected is not null ? "disconnected" : ready ? "configured" : "needs_credentials"));
        }
        return result.ToArray();
    }

    public async Task<IReadOnlyList<ConnectionInfo>> ListAsync(Guid organizationId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        return await ReadAsync(db, organizationId, null, ct);
    }

    public async Task<ConnectionCreated> CreateAsync(Guid org, Guid requestId, ConnectionDefinition definition, CancellationToken ct)
    {
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition));
        var id = Guid.NewGuid();
        var secret = definition.Push ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : null;
        var credential = secret is null ? null : PrepareCredential(org, id, "push", Guid.NewGuid().ToString("N"), secret, clock.GetUtcNow().AddDays(definition.PushKeyDays));
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var gate = Command(db, "SELECT pg_advisory_xact_lock(hashtextextended(@identity,0))", ("identity", org + ":" + requestId)))
            await gate.ExecuteNonQueryAsync(ct);
        Guid? existingId = null;
        await using (var existing = Command(db, "SELECT id,definition_hash FROM integrations.connections WHERE organization_id=@org AND request_id=@request", ("org", org), ("request", requestId)))
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, reader.GetFieldValue<byte[]>(1))) throw new ConnectionFailure(ConnectionFailureKind.Conflict);
                existingId = reader.GetGuid(0);
            }
        }
        if (existingId is not null)
        {
            var existing = (await ReadAsync(db, org, existingId, ct)).Single();
            await tx.CommitAsync(ct);
            // A network retry can recover identity, never reveal the original secret again.
            return new(existing, null);
        }
        var settings = definition.Settings;
        await using (var insert = Command(db, """
            INSERT INTO integrations.connections(id,organization_id,request_id,definition_hash,name,source_business_id,base_url,
                push,pull_live,pull_scheduled,price_max_age_seconds,stock_max_age_seconds,sync_interval_seconds,clock_skew_seconds,revision)
            VALUES(@id,@org,@request,@hash,@name,@business,@url,@push,@live,@scheduled,@price,@stock,@sync::int,@skew,@revision)
            """, ("id", id), ("org", org), ("request", requestId), ("hash", hash), ("name", settings.Name), ("business", definition.SourceBusinessId),
            ("url", definition.BaseUrl), ("push", definition.Push), ("live", definition.PullLive), ("scheduled", definition.PullScheduled),
            ("price", settings.PriceMaxAgeSeconds), ("stock", settings.StockMaxAgeSeconds), ("sync", settings.SyncIntervalSeconds),
            ("skew", settings.ClockSkewSeconds), ("revision", Guid.NewGuid())))
            await insert.ExecuteNonQueryAsync(ct);
        if (credential is not null) await AddCredentialAsync(db, org, id, credential, ct);
        var created = (await ReadAsync(db, org, id, ct)).Single();
        await tx.CommitAsync(ct);
        return new(created, secret);
    }

    private sealed record LockedConnection(bool Push, bool PullLive, bool PullScheduled, bool Disconnected);
    private static async Task<LockedConnection?> LockAsync(NpgsqlConnection db, Guid org, Guid id, Guid revision, bool allowDisconnected, CancellationToken ct)
    {
        await using var command = Command(db, "SELECT revision,disconnected_at,push,pull_live,pull_scheduled FROM integrations.connections WHERE organization_id=@org AND id=@id FOR UPDATE", ("org", org), ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var disconnected = !reader.IsDBNull(1);
        if (disconnected && !allowDisconnected) return null;
        if (!disconnected && reader.GetGuid(0) != revision) throw new ConnectionFailure(ConnectionFailureKind.Conflict);
        return new(reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), disconnected);
    }

    private static async Task NewRevisionAsync(NpgsqlConnection db, Guid org, Guid id, CancellationToken ct)
    {
        await using var command = Command(db, "UPDATE integrations.connections SET revision=@revision,updated_at=now() WHERE organization_id=@org AND id=@id", ("revision", Guid.NewGuid()), ("org", org), ("id", id));
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record PreparedCredential(string Direction, string KeyId, string Ciphertext, DateTimeOffset? ExpiresAt);
    private PreparedCredential PrepareCredential(Guid org, Guid id, string direction, string keyId, string secret, DateTimeOffset? expires)
    {
        // Data Protection may load/persist its key ring. Keep that file I/O outside database transactions.
        var encrypted = protection.CreateProtector("DocParse.Integrations.Credential.v1", org.ToString(), id.ToString(), direction, keyId).Protect(secret);
        return new(direction, keyId, encrypted, expires);
    }

    private static async Task AddCredentialAsync(NpgsqlConnection db, Guid org, Guid id, PreparedCredential credential, CancellationToken ct)
    {
        await using (var revoke = Command(db, "UPDATE integrations.credentials SET revoked_at=now(),protected_secret=NULL WHERE organization_id=@org AND connection_id=@id AND direction=@direction AND revoked_at IS NULL", ("org", org), ("id", id), ("direction", credential.Direction)))
            await revoke.ExecuteNonQueryAsync(ct);
        await using var add = Command(db, """
            INSERT INTO integrations.credentials(id,organization_id,connection_id,direction,key_id,protected_secret,expires_at)
            VALUES(@credential,@org,@id,@direction,@key,@secret,@expires::timestamptz)
            """, ("credential", Guid.NewGuid()), ("org", org), ("id", id), ("direction", credential.Direction), ("key", credential.KeyId), ("secret", credential.Ciphertext), ("expires", credential.ExpiresAt));
        await add.ExecuteNonQueryAsync(ct);
    }

    public async Task<ConnectionInfo?> UpdateAsync(Guid org, Guid id, Guid revision, ConnectionSettings settings, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var connection = await LockAsync(db, org, id, revision, false, ct);
        if (connection is null) return null;
        settings = settings.Validate(connection.PullScheduled);
        await using (var update = Command(db, """
            UPDATE integrations.connections SET name=@name,price_max_age_seconds=@price,stock_max_age_seconds=@stock,
                clock_skew_seconds=@skew,sync_interval_seconds=@sync::int WHERE organization_id=@org AND id=@id
            """, ("name", settings.Name), ("price", settings.PriceMaxAgeSeconds), ("stock", settings.StockMaxAgeSeconds),
            ("skew", settings.ClockSkewSeconds), ("sync", settings.SyncIntervalSeconds), ("org", org), ("id", id)))
            await update.ExecuteNonQueryAsync(ct);
        await NewRevisionAsync(db, org, id, ct);
        var result = (await ReadAsync(db, org, id, ct)).Single();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PushKeyIssued?> RotatePushAsync(Guid org, Guid id, Guid revision, int days, CancellationToken ct)
    {
        if (days is < 1 or > 365) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var credential = PrepareCredential(org, id, "push", Guid.NewGuid().ToString("N"), secret, clock.GetUtcNow().AddDays(days));
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var connection = await LockAsync(db, org, id, revision, false, ct);
        if (connection is null) return null;
        if (!connection.Push) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        await AddCredentialAsync(db, org, id, credential, ct);
        await NewRevisionAsync(db, org, id, ct);
        var result = (await ReadAsync(db, org, id, ct)).Single();
        await tx.CommitAsync(ct);
        return new(result, secret);
    }

    public async Task<ConnectionInfo?> SetPullKeyAsync(Guid org, Guid id, Guid revision, string keyId, string secretHex, CancellationToken ct)
    {
        secretHex = ConnectionDefinition.ValidateKey(keyId, secretHex);
        var credential = PrepareCredential(org, id, "pull", keyId, secretHex, null);
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var connection = await LockAsync(db, org, id, revision, false, ct);
        if (connection is null) return null;
        if (!connection.PullLive && !connection.PullScheduled) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        await AddCredentialAsync(db, org, id, credential, ct);
        await NewRevisionAsync(db, org, id, ct);
        var result = (await ReadAsync(db, org, id, ct)).Single();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<bool> DisconnectAsync(Guid org, Guid id, Guid revision, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var connection = await LockAsync(db, org, id, revision, true, ct);
        if (connection is null) return false;
        if (!connection.Disconnected)
        {
            await using (var disconnect = Command(db, "UPDATE integrations.connections SET disconnected_at=now() WHERE organization_id=@org AND id=@id", ("org", org), ("id", id)))
                await disconnect.ExecuteNonQueryAsync(ct);
            await using (var revoke = Command(db, "UPDATE integrations.credentials SET revoked_at=COALESCE(revoked_at,now()),protected_secret=NULL WHERE organization_id=@org AND connection_id=@id", ("org", org), ("id", id)))
                await revoke.ExecuteNonQueryAsync(ct);
            await NewRevisionAsync(db, org, id, ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }
}
