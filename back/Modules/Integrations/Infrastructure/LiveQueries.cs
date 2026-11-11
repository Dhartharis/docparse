using System.Security.Cryptography;
using System.Text.Json;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.OperationalData.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class LiveQueries(IConfiguration configuration, IDataProtectionProvider protection, ProviderPolicy policy,
    IProviderTransport transport, IOperationalProjection projection, TimeProvider clock) : ILiveQueries
{
    private sealed record Source(OperationalSource Scope, string BaseUrl, Guid CredentialId, string KeyId, string Ciphertext);
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct); return db;
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, string text, Guid org, Guid id)
    {
        var command = new NpgsqlCommand(text,db); command.Parameters.AddWithValue("org",org); command.Parameters.AddWithValue("id",id); return command;
    }
    private async Task<Source> ResolveAsync(Guid org, Guid id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db,"""
            SELECT c.revision,c.source_business_id,c.price_max_age_seconds,c.stock_max_age_seconds,c.clock_skew_seconds,c.push,
                c.base_url,k.id,k.key_id,k.protected_secret FROM integrations.connections c
            LEFT JOIN integrations.credentials k ON k.organization_id=c.organization_id AND k.connection_id=c.id AND k.direction='pull' AND k.revoked_at IS NULL
            WHERE c.organization_id=@org AND c.id=@id AND c.pull_live AND c.disconnected_at IS NULL
            """,org,id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new LiveQueryFailure("not_found");
        if (reader.IsDBNull(7)) throw new LiveQueryFailure("connection_not_ready");
        return new(new(id,org,reader.GetGuid(0),reader.GetString(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetBoolean(5)),
            reader.GetString(6),reader.GetGuid(7),reader.GetString(8),reader.GetString(9));
    }
    private static NpgsqlCommand Fence(NpgsqlConnection db, Source source, bool locked)
    {
        var command = Command(db,"""
            SELECT c.id FROM integrations.connections c WHERE c.organization_id=@org AND c.id=@id AND c.revision=@revision
                AND c.pull_live AND c.disconnected_at IS NULL AND EXISTS(SELECT 1 FROM integrations.credentials k
                    WHERE k.connection_id=c.id AND k.id=@credential AND k.direction='pull' AND k.revoked_at IS NULL)
            """+(locked ? " FOR UPDATE" : ""),source.Scope.OrganizationId,source.Scope.Id);
        command.Parameters.AddWithValue("revision",source.Scope.Revision); command.Parameters.AddWithValue("credential",source.CredentialId); return command;
    }
    private async Task AdmitAsync(Source source, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        await using (var fence = Fence(db,source,true))
            if (await fence.ExecuteScalarAsync(ct) is null) throw new LiveQueryFailure("connection_conflict");
        await using (var count = new NpgsqlCommand("""
            INSERT INTO integrations.live_query_windows(connection_id,window_start,count) VALUES(@id,date_trunc('minute',now()),1)
            ON CONFLICT(connection_id,window_start) DO UPDATE SET count=live_query_windows.count+1 WHERE live_query_windows.count<30 RETURNING count
            """,db))
        {
            count.Parameters.AddWithValue("id",source.Scope.Id);
            if (await count.ExecuteScalarAsync(ct) is null) throw new LiveQueryFailure("provider_rate",60);
        }
        await using (var cleanup = new NpgsqlCommand("DELETE FROM integrations.live_query_windows WHERE connection_id=@id AND window_start<now()-interval '1 hour'",db))
        { cleanup.Parameters.AddWithValue("id",source.Scope.Id); await cleanup.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }
    public async Task<LiveQueryResponse> QueryAsync(Guid organizationId, Guid connectionId, LiveQuery query, CancellationToken ct)
    {
        LiveQueryRules.Validate(query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var source = await ResolveAsync(organizationId,connectionId,deadline.Token);
            string secret;
            try { secret = protection.CreateProtector("DocParse.Integrations.Credential.v1",organizationId.ToString(),connectionId.ToString(),"pull",source.KeyId).Unprotect(source.Ciphertext); }
            catch (CryptographicException) { throw new LiveQueryFailure("connection_not_ready"); }
            var endpoint = ProviderDestination.Endpoint(source.BaseUrl,policy);
            await AdmitAsync(source,deadline.Token);
            var selector = new Dictionary<string,object> { [query.SelectorKind]=query.SelectorKind == "text" ? query.Values[0] : query.Values };
            var body = JsonSerializer.SerializeToUtf8Bytes(new { contractVersion="1",sourceBusinessId=source.Scope.SourceBusinessId,fields=query.Fields,selector });
            var bytes = await transport.QueryAsync(endpoint,source.KeyId,secret,body,deadline.Token);
            var now = clock.GetUtcNow();
            var results = LiveResponseCodec.Parse(bytes,source.Scope.SourceBusinessId,query,projection,now,source.Scope.ClockSkewSeconds);
            var views = new List<LiveQueryResult>();
            foreach (var result in results)
            {
                var records = await projection.CheckLiveAsync(organizationId,connectionId,result.Offers,source.Scope.PriceMaxAgeSeconds,source.Scope.StockMaxAgeSeconds,now,deadline.Token);
                views.Add(new(result.SelectorValue,result.Status,records));
            }
            await using var db = await OpenAsync(deadline.Token);
            await using var fence = Fence(db,source,false);
            if (await fence.ExecuteScalarAsync(deadline.Token) is null) throw new LiveQueryFailure("connection_conflict");
            return new(now,views);
        }
        catch (ConnectionFailure) { throw new LiveQueryFailure("connection_origin"); }
        catch (OperationalDataInvalid) { throw new LiveQueryFailure("provider_response"); }
        catch (HttpRequestException) { throw new LiveQueryFailure("provider_unavailable"); }
        catch (NpgsqlException) { throw new LiveQueryFailure("provider_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LiveQueryFailure("provider_unavailable"); }
    }
}
