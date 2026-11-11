using System.Numerics;
using System.Text.Json;
using DocParse.Modules.OperationalData.Application;
using DocParse.Modules.OperationalData.Contracts;
using Npgsql;

namespace DocParse.Modules.OperationalData.Infrastructure;

public sealed class OperationalProjection(IConfiguration configuration) : IOperationalProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Validate(OperationalChange change, DateTimeOffset now, int clockSkewSeconds) => OperationalRules.Validate(change, now, clockSkewSeconds);
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct);
        return db;
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params (string Name, object Value)[] values)
    {
        var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    public async Task<ProjectionOutcome> ApplyAsync(Guid org, Guid connection, OperationalChange change, DateTimeOffset now, int skew,
        System.Data.Common.DbTransaction transaction, CancellationToken ct)
    {
        Validate(change, now, skew);
        var id = change.Offer?.ExternalId ?? change.Tombstone!.ExternalId;
        var version = change.Offer?.Version ?? change.Tombstone!.Version;
        var deleted = change.Tombstone is not null;
        var payload = change.Offer is not null ? JsonSerializer.Serialize(change.Offer, Json) : JsonSerializer.Serialize(change.Tombstone, Json);
        var db = transaction.Connection as NpgsqlConnection ?? throw new InvalidOperationException("An active local PostgreSQL transaction is required.");
        await using (var add = Command(db, """
            INSERT INTO operational_data.records(organization_id,connection_id,external_id,version,is_deleted,payload)
            VALUES(@org,@connection,@id,@version::numeric,@deleted,@payload::jsonb) ON CONFLICT DO NOTHING RETURNING external_id
            """, ("org", org), ("connection", connection), ("id", id), ("version", version), ("deleted", deleted), ("payload", payload)))
            if (await add.ExecuteScalarAsync(ct) is not null) return ProjectionOutcome.Applied;
        string current;
        bool equal;
        await using (var read = Command(db, """
            SELECT version::text,is_deleted=@deleted AND payload=@payload::jsonb FROM operational_data.records
            WHERE organization_id=@org AND connection_id=@connection AND external_id=@id FOR UPDATE
            """, ("org", org), ("connection", connection), ("id", id), ("deleted", deleted), ("payload", payload)))
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct); current = reader.GetString(0); equal = reader.GetBoolean(1);
        }
        var comparison = BigInteger.Parse(version, System.Globalization.CultureInfo.InvariantCulture)
            .CompareTo(BigInteger.Parse(current, System.Globalization.CultureInfo.InvariantCulture));
        if (comparison <= 0)
        {
            return comparison < 0 ? ProjectionOutcome.IgnoredOlderVersion : equal ? ProjectionOutcome.Applied : ProjectionOutcome.VersionConflict;
        }
        await using (var update = Command(db, """
            UPDATE operational_data.records SET version=@version::numeric,is_deleted=@deleted,payload=@payload::jsonb,updated_at=now()
            WHERE organization_id=@org AND connection_id=@connection AND external_id=@id
            """, ("org", org), ("connection", connection), ("id", id), ("version", version), ("deleted", deleted), ("payload", payload)))
            await update.ExecuteNonQueryAsync(ct);
        return ProjectionOutcome.Applied;
    }

    public async Task<OperationalSnapshot> ReadAsync(Guid org, Guid connection, int priceAge, int stockAge, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        long total;
        await using (var count = Command(db, "SELECT count(*) FROM operational_data.records WHERE organization_id=@org AND connection_id=@connection AND NOT is_deleted", ("org", org), ("connection", connection)))
            total = (long)(await count.ExecuteScalarAsync(ct))!;
        var records = new List<OperationalRecordView>();
        await using (var read = Command(db, "SELECT payload::text FROM operational_data.records WHERE organization_id=@org AND connection_id=@connection AND NOT is_deleted ORDER BY external_id LIMIT 100", ("org", org), ("connection", connection)))
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var offer = JsonSerializer.Deserialize<OperationalOffer>(reader.GetString(0), Json) ?? throw new InvalidDataException();
                var price = offer.Price;
                var stock = offer.Availability;
                records.Add(new(offer,
                    offer.Active && price.Known && now < price.ValidUntil && now < price.ObservedAt?.AddSeconds(priceAge),
                    offer.Active && stock.Tracking == "tracked" && now < stock.ValidUntil && now < stock.ObservedAt.AddSeconds(stockAge)));
            }
        }
        await tx.CommitAsync(ct);
        return new(total, records);
    }
    public async Task<IReadOnlyList<OperationalRecordView>> CheckLiveAsync(Guid org, Guid connection, IReadOnlyList<OperationalOffer> offers, int priceAge, int stockAge, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var records = new List<OperationalRecordView>();
        foreach (var offer in offers)
        {
            await using var command = Command(db,"SELECT version::text,is_deleted,payload::text FROM operational_data.records WHERE organization_id=@org AND connection_id=@connection AND external_id=@id",
                ("org",org),("connection",connection),("id",offer.ExternalId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                LiveEvidence.Check(offer,reader.GetString(0),reader.GetBoolean(1),reader.GetBoolean(1) ? null : JsonSerializer.Deserialize<OperationalOffer>(reader.GetString(2),Json));
            records.Add(LiveEvidence.View(offer,priceAge,stockAge,now));
        }
        await tx.CommitAsync(ct); return records;
    }
}
