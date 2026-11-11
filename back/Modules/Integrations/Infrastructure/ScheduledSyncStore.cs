using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.Integrations.Application;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed record SyncStatus(string State, int Attempts, DateTimeOffset DueAt, string? ErrorCode, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastCompletedAt, bool SnapshotCompleted);
public sealed record SyncWork(Guid OrganizationId, Guid ConnectionId, Guid Generation, Guid Revision, Guid CredentialId, Guid LeaseId,
    int Attempts, int Interval, int Skew, string Business, string BaseUrl, string KeyId, string ProtectedSecret, string? ProtectedCursor);

public sealed class ScheduledSyncStore(IConfiguration configuration)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) {var db=new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);await db.OpenAsync(ct);return db;}
    private static NpgsqlCommand Command(NpgsqlConnection db,string sql,params (string Name,object Value)[] values)
    {var command=new NpgsqlCommand(sql,db);foreach(var(name,value)in values)command.Parameters.AddWithValue(name,value);return command;}
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using(var create=Command(db,"""
            INSERT INTO integrations.sync_jobs(connection_id,organization_id,generation,configuration_revision,credential_id,state)
            SELECT c.id,c.organization_id,gen_random_uuid(),c.revision,k.id,'waiting' FROM integrations.connections c
            LEFT JOIN integrations.credentials k ON k.connection_id=c.id AND k.direction='pull' AND k.revoked_at IS NULL
            WHERE c.pull_scheduled AND c.disconnected_at IS NULL ON CONFLICT DO NOTHING
            """))await create.ExecuteNonQueryAsync(ct);
        await using(var changed=Command(db,"""
            UPDATE integrations.sync_jobs j SET configuration_revision=c.revision,credential_id=k.id,
                generation=CASE WHEN j.credential_id IS DISTINCT FROM k.id THEN gen_random_uuid() ELSE j.generation END,
                protected_cursor=CASE WHEN j.credential_id IS DISTINCT FROM k.id THEN NULL ELSE j.protected_cursor END,
                snapshot_completed=CASE WHEN j.credential_id IS DISTINCT FROM k.id THEN false ELSE j.snapshot_completed END,
                state='waiting',attempts=0,due_at=now(),lease_id=NULL,lease_until=NULL,error_code=NULL,updated_at=now()
            FROM integrations.connections c LEFT JOIN integrations.credentials k ON k.connection_id=c.id AND k.direction='pull' AND k.revoked_at IS NULL
            WHERE j.connection_id=c.id AND c.pull_scheduled AND c.disconnected_at IS NULL
                AND (j.configuration_revision<>c.revision OR j.credential_id IS DISTINCT FROM k.id)
            """))await changed.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<SyncWork?> ClaimAsync(CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using(var exhausted=Command(db,"UPDATE integrations.sync_jobs SET state='failed',error_code='sync_failed',lease_id=NULL,lease_until=NULL,updated_at=now() WHERE state='processing' AND lease_until<=now() AND attempts>=5"))await exhausted.ExecuteNonQueryAsync(ct);
        Guid? id=null;var lease=Guid.NewGuid();
        await using(var claim=Command(db,"""
            UPDATE integrations.sync_jobs SET state='processing',attempts=attempts+1,lease_id=@lease,lease_until=now()+interval '2 minutes',updated_at=now()
            WHERE connection_id=(SELECT j.connection_id FROM integrations.sync_jobs j JOIN integrations.connections c ON c.id=j.connection_id
                WHERE c.pull_scheduled AND c.disconnected_at IS NULL AND j.attempts<5 AND
                    ((j.state='waiting' AND j.due_at<=now()) OR(j.state='processing' AND j.lease_until<=now()))
                ORDER BY j.due_at,j.connection_id FOR UPDATE OF j SKIP LOCKED LIMIT 1) RETURNING connection_id
            """,("lease",lease)))id=await claim.ExecuteScalarAsync(ct) as Guid?;
        if(id is null){await tx.CommitAsync(ct);return null;}
        SyncWork? work=null;
        await using(var read=Command(db,"""
            SELECT j.organization_id,j.generation,j.configuration_revision,j.credential_id,j.attempts,j.protected_cursor,
                c.sync_interval_seconds,c.clock_skew_seconds,c.source_business_id,c.base_url,k.key_id,k.protected_secret
            FROM integrations.sync_jobs j JOIN integrations.connections c ON c.id=j.connection_id
            LEFT JOIN integrations.credentials k ON k.id=j.credential_id AND k.connection_id=j.connection_id AND k.revoked_at IS NULL
            WHERE j.connection_id=@id
            """,("id",id.Value)))
        await using(var reader=await read.ExecuteReaderAsync(ct))
        {
            if(await reader.ReadAsync(ct) && !reader.IsDBNull(10))work=new(reader.GetGuid(0),id.Value,reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),lease,reader.GetInt32(4),reader.GetInt32(6),reader.GetInt32(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.IsDBNull(5)?null:reader.GetString(5));
        }
        if(work is null)
        {await using var stop=Command(db,"UPDATE integrations.sync_jobs SET state='failed',error_code='connection_not_ready',lease_id=NULL,lease_until=NULL WHERE connection_id=@id",("id",id.Value));await stop.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);return work;
    }
    private static async Task<bool> FenceAsync(NpgsqlConnection db,SyncWork work,CancellationToken ct)
    {
        // All ingestion paths lock the connection before their work row and projection lock.
        await using(var connection=Command(db,"""
        SELECT c.id FROM integrations.connections c
        WHERE c.id=@id AND c.organization_id=@org AND c.revision=@revision AND c.pull_scheduled AND c.disconnected_at IS NULL
            AND EXISTS(SELECT 1 FROM integrations.credentials k WHERE k.id=@credential AND k.connection_id=c.id AND k.revoked_at IS NULL)
        FOR UPDATE
        """,("id",work.ConnectionId),("org",work.OrganizationId),("revision",work.Revision),("credential",work.CredentialId)))
            if(await connection.ExecuteScalarAsync(ct)is null)return false;
        await using var job=Command(db,"SELECT connection_id FROM integrations.sync_jobs WHERE connection_id=@id AND generation=@generation AND lease_id=@lease AND state='processing' AND lease_until>clock_timestamp() FOR UPDATE",
            ("id",work.ConnectionId),("generation",work.Generation),("lease",work.LeaseId));
        return await job.ExecuteScalarAsync(ct)is not null;
    }
    public async Task ApplyAsync(SyncWork work,OperationalPage page,string protectedCursor,IOperationalProjection projection,DateTimeOffset now,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        if(!await FenceAsync(db,work,ct))return;
        await projection.ApplySyncPageAsync(work.OrganizationId,work.ConnectionId,work.Generation,page,now,work.Skew,tx,ct);
        await using(var finish=Command(db,"""
            UPDATE integrations.sync_jobs SET protected_cursor=@cursor,state='waiting',attempts=0,error_code=NULL,lease_id=NULL,lease_until=NULL,
                last_success_at=now(),last_completed_at=CASE WHEN @complete THEN now() ELSE last_completed_at END,
                snapshot_completed=snapshot_completed OR @snapshot_complete,due_at=now()+make_interval(secs=>@delay),updated_at=now()
            WHERE connection_id=@id AND generation=@generation AND lease_id=@lease AND lease_until>clock_timestamp()
            """,("cursor",protectedCursor),("complete",page.Complete),("snapshot_complete",page.Mode=="snapshot"&&page.Complete),
            ("delay",page.Complete?work.Interval:2),("id",work.ConnectionId),("generation",work.Generation),("lease",work.LeaseId)))
            if(await finish.ExecuteNonQueryAsync(ct)!=1)return;
        await tx.CommitAsync(ct);
    }
    public async Task FailAsync(SyncWork work,string code,bool retryable,int? retryAfter,bool cursorExpired,CancellationToken ct)
    {
        var action=SyncRetryPolicy.Decide(work.Attempts,retryable,retryAfter,cursorExpired,work.ProtectedCursor is not null,work.Interval);
        var restart=action.Restart;var retry=action.Retry;var delay=action.DelaySeconds;
        if(cursorExpired&&!restart)code="provider_response";
        await using var db=await OpenAsync(ct);
        await using var command=Command(db,"""
            UPDATE integrations.sync_jobs SET state=@state,error_code=@code,attempts=CASE WHEN @restart THEN 0 ELSE attempts END,
                generation=CASE WHEN @restart THEN gen_random_uuid() ELSE generation END,
                protected_cursor=CASE WHEN @restart THEN NULL ELSE protected_cursor END,
                snapshot_completed=CASE WHEN @restart THEN false ELSE snapshot_completed END,
                due_at=now()+make_interval(secs=>@delay),lease_id=NULL,lease_until=NULL,updated_at=now()
            WHERE connection_id=@id AND generation=@generation AND lease_id=@lease AND state='processing' AND lease_until>clock_timestamp()
            """,("state",restart||retry?"waiting":"failed"),("code",code),("restart",restart),("delay",delay),("id",work.ConnectionId),("generation",work.Generation),("lease",work.LeaseId));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<SyncStatus?> StatusAsync(Guid org,Guid id,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);
        await using var command=Command(db,"""
            SELECT coalesce(j.state,'waiting'),coalesce(j.attempts,0),coalesce(j.due_at,now()),j.error_code,j.last_success_at,j.last_completed_at,coalesce(j.snapshot_completed,false)
            FROM integrations.connections c LEFT JOIN integrations.sync_jobs j ON j.connection_id=c.id
            WHERE c.organization_id=@org AND c.id=@id AND c.pull_scheduled AND c.disconnected_at IS NULL
            """,("org",org),("id",id));
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?new(configuration.GetValue("Integrations:ScheduledSyncEnabled",true)?reader.GetString(0):"paused",reader.GetInt32(1),reader.GetFieldValue<DateTimeOffset>(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5),reader.GetBoolean(6)):null;
    }
    public async Task<bool> RestartAsync(Guid org,Guid id,Guid revision,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using(var active=Command(db,"SELECT id FROM integrations.connections WHERE organization_id=@org AND id=@id AND revision=@revision AND pull_scheduled AND disconnected_at IS NULL FOR UPDATE",("org",org),("id",id),("revision",revision)))
            if(await active.ExecuteScalarAsync(ct)is null)return false;
        await using var restart=Command(db,"""
            INSERT INTO integrations.sync_jobs(organization_id,connection_id,generation,configuration_revision,state)
            VALUES(@org,@id,gen_random_uuid(),@revision,'waiting')
            ON CONFLICT(connection_id) DO UPDATE SET generation=gen_random_uuid(),protected_cursor=NULL,state='waiting',attempts=0,due_at=now(),error_code=NULL,lease_id=NULL,lease_until=NULL,snapshot_completed=false,updated_at=now()
            """,("org",org),("id",id),("revision",revision));
        await restart.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
    }
}
