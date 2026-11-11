using System.Numerics;
using System.Data.Common;
using DocParse.Modules.OperationalData.Contracts;
using Npgsql;

namespace DocParse.Modules.OperationalData.Infrastructure;

public sealed partial class OperationalProjection
{
    private static BigInteger Number(string value)=>BigInteger.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
    private static async Task LockSourceAsync(NpgsqlConnection db, Guid org, Guid connection, CancellationToken ct)
    {
        await using var command=Command(db,"SELECT pg_advisory_xact_lock(hashtextextended(@scope,0))",("scope",$"operational:{org}:{connection}"));
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string?> FloorAsync(NpgsqlConnection db, Guid org, Guid connection, CancellationToken ct)
    {
        await using var command=Command(db,"SELECT version::text FROM operational_data.snapshot_floors WHERE organization_id=@org AND connection_id=@connection",("org",org),("connection",connection));
        return await command.ExecuteScalarAsync(ct) as string;
    }
    public async Task ApplySyncPageAsync(Guid org, Guid connection, Guid generation, OperationalPage page, DateTimeOffset now, int skew, DbTransaction transaction, CancellationToken ct)
    {
        var db=transaction.Connection as NpgsqlConnection ?? throw new InvalidOperationException("An active local PostgreSQL transaction is required.");
        await LockSourceAsync(db,org,connection,ct);
        var floor=await FloorAsync(db,org,connection,ct);
        Guid? storedGeneration=null; string? snapshot=null,watermark=null,last=null;bool completed=false;
        await using(var read=Command(db,"SELECT generation,snapshot_id,watermark::text,completed,last_version::text FROM operational_data.sync_state WHERE organization_id=@org AND connection_id=@connection FOR UPDATE",("org",org),("connection",connection)))
        await using(var reader=await read.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct)){storedGeneration=reader.GetGuid(0);snapshot=reader.GetString(1);watermark=reader.GetString(2);completed=reader.GetBoolean(3);last=reader.GetString(4);}
        if(storedGeneration!=generation)
        {
            if(page.Mode!="snapshot" || page.SnapshotWatermark is null || page.SnapshotId is null ||
                floor is not null && Number(page.SnapshotWatermark)<Number(floor) || last is not null && Number(page.SnapshotWatermark!)<Number(last)) throw new OperationalDataInvalid();
            snapshot=page.SnapshotId;watermark=page.SnapshotWatermark;last=watermark;completed=false;
            await using(var clear=Command(db,"DELETE FROM operational_data.snapshot_seen WHERE organization_id=@org AND connection_id=@connection",("org",org),("connection",connection))) await clear.ExecuteNonQueryAsync(ct);
            await using var begin=Command(db,"""
                INSERT INTO operational_data.sync_state(organization_id,connection_id,generation,snapshot_id,watermark,completed,last_version)
                VALUES(@org,@connection,@generation,@snapshot,@watermark::numeric,false,@watermark::numeric)
                ON CONFLICT(organization_id,connection_id) DO UPDATE SET generation=@generation,snapshot_id=@snapshot,watermark=@watermark::numeric,completed=false,last_version=@watermark::numeric
                """,("org",org),("connection",connection),("generation",generation),("snapshot",snapshot),("watermark",watermark));
            await begin.ExecuteNonQueryAsync(ct);
        }
        if(!completed && (page.Mode!="snapshot" || page.SnapshotId!=snapshot || page.SnapshotWatermark!=watermark) || completed && page.Mode!="incremental") throw new OperationalDataInvalid();
        foreach(var change in page.Changes)
        {
            Validate(change,now,skew);
            var version=change.Offer?.Version ?? change.Tombstone!.Version;
            if(page.Mode=="snapshot")
            {
                if(change.Offer is null || Number(version)>Number(watermark!)) throw new OperationalDataInvalid();
                await using var seen=Command(db,"INSERT INTO operational_data.snapshot_seen(organization_id,connection_id,external_id) VALUES(@org,@connection,@id) ON CONFLICT DO NOTHING",("org",org),("connection",connection),("id",change.Offer.ExternalId));
                await seen.ExecuteNonQueryAsync(ct);
            }
            else
            {
                if(Number(version)<=Number(watermark!) || Number(version)<Number(last!)) throw new OperationalDataInvalid();
                last=version;
            }
            if(await ApplyAsync(org,connection,change,now,skew,transaction,ct)==ProjectionOutcome.VersionConflict) throw new OperationalDataInvalid();
        }
        if(page.Mode=="snapshot" && page.Complete)
        {
            await using(var absent=Command(db,"""
                DELETE FROM operational_data.records r WHERE r.organization_id=@org AND r.connection_id=@connection AND r.version<=@watermark::numeric
                    AND NOT EXISTS(SELECT 1 FROM operational_data.snapshot_seen s WHERE s.organization_id=r.organization_id AND s.connection_id=r.connection_id AND s.external_id=r.external_id)
                """,("org",org),("connection",connection),("watermark",watermark!))) await absent.ExecuteNonQueryAsync(ct);
            await using(var advance=Command(db,"""
                INSERT INTO operational_data.snapshot_floors(organization_id,connection_id,version) VALUES(@org,@connection,@watermark::numeric)
                ON CONFLICT(organization_id,connection_id) DO UPDATE SET version=greatest(snapshot_floors.version,EXCLUDED.version)
                """,("org",org),("connection",connection),("watermark",watermark!))) await advance.ExecuteNonQueryAsync(ct);
            completed=true;
            await using var clear=Command(db,"DELETE FROM operational_data.snapshot_seen WHERE organization_id=@org AND connection_id=@connection",("org",org),("connection",connection));await clear.ExecuteNonQueryAsync(ct);
        }
        await using var save=Command(db,"UPDATE operational_data.sync_state SET completed=@completed,last_version=@last::numeric WHERE organization_id=@org AND connection_id=@connection",("org",org),("connection",connection),("completed",completed),("last",last!));
        await save.ExecuteNonQueryAsync(ct);
    }
}
