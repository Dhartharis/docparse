using DocParse.Modules.Integrations.Contracts;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class ConnectionCatalog(IConfiguration configuration) : IConnectionCatalog
{
    public async Task<OperationalSource?> GetActiveAsync(Guid organizationId, Guid connectionId, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT revision,source_business_id,price_max_age_seconds,stock_max_age_seconds,clock_skew_seconds,push
            FROM integrations.connections WHERE organization_id=@org AND id=@id AND disconnected_at IS NULL
            """, db);
        command.Parameters.AddWithValue("org", organizationId); command.Parameters.AddWithValue("id", connectionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(connectionId, organizationId, reader.GetGuid(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetBoolean(5)) : null;
    }
}
