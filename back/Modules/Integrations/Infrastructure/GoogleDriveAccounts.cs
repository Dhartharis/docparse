using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Application;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class GoogleDriveAccounts(IConfiguration configuration,IDataProtectionProvider protection,TimeProvider clock):IGoogleDriveAccounts
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct){var db=new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);await db.OpenAsync(ct);return db;}
    private static NpgsqlCommand Command(NpgsqlConnection db,string sql,params(string Name,object? Value)[] values)
    {
        var command=new NpgsqlCommand(sql,db);
        foreach(var(name,value)in values)command.Parameters.Add(value is null?new NpgsqlParameter(name,NpgsqlDbType.Text){Value=DBNull.Value}:new NpgsqlParameter(name,value));
        return command;
    }
    private IDataProtector Protector(Guid org,Guid user,Guid account,string purpose)=>protection.CreateProtector("DocParse.GoogleDrive.v1",org.ToString(),user.ToString(),account.ToString(),purpose);
    private static byte[] Hash(string state)
    {if(!Regex.IsMatch(state,@"\A[a-f0-9]{64}\z"))throw new GoogleDriveFailure("google_authorization");return SHA256.HashData(Encoding.ASCII.GetBytes(state));}
    public async Task StartAsync(Guid org,Guid user,Guid account,string state,string verifier,CancellationToken ct)
    {
        var encrypted=Protector(org,user,account,"verifier").Protect(verifier);var hash=Hash(state);var now=clock.GetUtcNow();
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using var command=Command(db,"""
            DELETE FROM integrations.google_authorizations WHERE organization_id=@org AND user_id=@user AND expires_at<@now;
            DELETE FROM integrations.google_accounts WHERE organization_id=@org AND user_id=@user AND protected_tokens IS NULL AND created_at<@old;
            INSERT INTO integrations.google_accounts(id,organization_id,user_id,revision) VALUES(@id,@org,@user,@revision);
            INSERT INTO integrations.google_authorizations(state_hash,organization_id,user_id,account_id,protected_verifier,expires_at)
            VALUES(@hash,@org,@user,@id,@verifier,@expires);
            """,("org",org),("user",user),("id",account),("revision",Guid.NewGuid()),("hash",hash),("verifier",encrypted),("expires",now.AddMinutes(10)),("now",now),("old",now.AddDays(-1)));
        await command.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<GoogleAuthorizationClaim> ClaimAsync(Guid org,Guid user,string state,CancellationToken ct)
    {
        var hash=Hash(state);Guid id;string? encrypted;bool completed;
        await using(var db=await OpenAsync(ct))
        {
            await using var command=Command(db,"""
                WITH pending AS(SELECT a.account_id,a.protected_verifier,a.completed_at FROM integrations.google_authorizations a
                  JOIN integrations.google_accounts g ON g.id=a.account_id WHERE a.organization_id=@org AND a.user_id=@user AND a.state_hash=@hash
                  AND a.expires_at>@now AND g.disconnected_at IS NULL AND (a.claimed_at IS NULL OR a.completed_at IS NOT NULL) FOR UPDATE OF a)
                UPDATE integrations.google_authorizations a SET claimed_at=COALESCE(a.claimed_at,@now),protected_verifier=NULL
                FROM pending p WHERE a.state_hash=@hash RETURNING p.account_id,p.protected_verifier,p.completed_at IS NOT NULL
                """,("org",org),("user",user),("hash",hash),("now",clock.GetUtcNow()));
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new GoogleDriveFailure("google_authorization");
            id=reader.GetGuid(0);encrypted=reader.IsDBNull(1)?null:reader.GetString(1);completed=reader.GetBoolean(2);
        }
        try{return new(id,encrypted is null?null:Protector(org,user,id,"verifier").Unprotect(encrypted),completed);}
        catch(CryptographicException){throw new GoogleDriveFailure("google_authorization");}
    }
    public async Task CompleteAsync(Guid org,Guid user,string state,Guid account,GoogleTokens tokens,CancellationToken ct)
    {
        var encrypted=Protector(org,user,account,"tokens").Protect(JsonSerializer.Serialize(tokens));var hash=Hash(state);
        await using var db=await OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using var command=Command(db,"""
            UPDATE integrations.google_accounts g SET protected_tokens=@tokens FROM integrations.google_authorizations a
            WHERE g.organization_id=@org AND g.user_id=@user AND g.id=@id AND g.disconnected_at IS NULL AND a.account_id=g.id
            AND a.state_hash=@hash AND a.claimed_at IS NOT NULL AND a.completed_at IS NULL AND a.expires_at>@now;
            """,("org",org),("user",user),("id",account),("hash",hash),("tokens",encrypted),("now",clock.GetUtcNow()));
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new GoogleDriveFailure("google_authorization");
        await using var done=Command(db,"UPDATE integrations.google_authorizations SET completed_at=@now WHERE state_hash=@hash",("hash",hash),("now",clock.GetUtcNow()));
        await done.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<GoogleAccount>> ListAsync(Guid org,Guid user,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var command=Command(db,"SELECT id,created_at,needs_reconnect OR(refresh_lease_until IS NOT NULL AND refresh_lease_until<=@now) FROM integrations.google_accounts WHERE organization_id=@org AND user_id=@user AND disconnected_at IS NULL AND(protected_tokens IS NOT NULL OR needs_reconnect) ORDER BY created_at DESC,id",("org",org),("user",user),("now",clock.GetUtcNow()));
        await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<GoogleAccount>();while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetBoolean(2)));return result;
    }
    public async Task<GoogleTokenLease> AcquireAsync(Guid org,Guid user,Guid account,CancellationToken ct)
    {
        string encrypted;Guid revision;Guid? lease=null;var now=clock.GetUtcNow();
        await using(var db=await OpenAsync(ct))
        await using(var tx=await db.BeginTransactionAsync(ct))
        {
            await using(var command=Command(db,"SELECT protected_tokens,revision,needs_reconnect,refresh_lease_until FROM integrations.google_accounts WHERE organization_id=@org AND user_id=@user AND id=@id AND disconnected_at IS NULL FOR UPDATE",("org",org),("user",user),("id",account)))
            await using(var reader=await command.ExecuteReaderAsync(ct))
            {
                if(!await reader.ReadAsync(ct)||reader.IsDBNull(0)||reader.GetBoolean(2))throw new GoogleDriveFailure("google_authorization");
                if(!reader.IsDBNull(3))throw new GoogleDriveFailure(reader.GetFieldValue<DateTimeOffset>(3)>now?"google_busy":"google_authorization");
                encrypted=reader.GetString(0);revision=reader.GetGuid(1);
            }
            // Claim before key-ring/token I/O. Every access briefly claims; release without refresh if the token is still fresh.
            lease=Guid.NewGuid();await using var claim=Command(db,"UPDATE integrations.google_accounts SET refresh_lease=@lease,refresh_lease_until=@until WHERE id=@id",("id",account),("lease",lease),("until",now.AddSeconds(30)));
            await claim.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
        }
        GoogleTokens tokens;
        try{tokens=JsonSerializer.Deserialize<GoogleTokens>(Protector(org,user,account,"tokens").Unprotect(encrypted))??throw new CryptographicException();}
        catch(Exception error)when(error is CryptographicException or JsonException)
        {await SaveRefreshAsync(org,user,account,new(revision,lease,new("","",now,null)),null,ct);throw new GoogleDriveFailure("google_authorization");}
        var result=new GoogleTokenLease(revision,lease,tokens);
        if(tokens.RefreshExpiresAt<=now)
        {await SaveRefreshAsync(org,user,account,result,null,ct);throw new GoogleDriveFailure("google_authorization");}
        if(tokens.ExpiresAt>now.AddSeconds(360))
        {
            if(!await SaveRefreshAsync(org,user,account,result,tokens,ct))throw new GoogleDriveFailure("google_authorization");
            return result with{LeaseId=null};
        }
        return result;
    }
    public async Task<bool> SaveRefreshAsync(Guid org,Guid user,Guid account,GoogleTokenLease lease,GoogleTokens? tokens,CancellationToken ct)
    {
        var encrypted=tokens is null?null:Protector(org,user,account,"tokens").Protect(JsonSerializer.Serialize(tokens));
        await using var db=await OpenAsync(ct);await using var command=Command(db,"""
            UPDATE integrations.google_accounts SET protected_tokens=@tokens,needs_reconnect=@failed,refresh_lease=NULL,refresh_lease_until=NULL
            WHERE organization_id=@org AND user_id=@user AND id=@id AND revision=@revision AND refresh_lease=@lease AND refresh_lease_until>@now AND disconnected_at IS NULL
            """,("org",org),("user",user),("id",account),("revision",lease.Revision),("lease",lease.LeaseId),("tokens",encrypted),("failed",tokens is null),("now",clock.GetUtcNow()));
        return await command.ExecuteNonQueryAsync(ct)==1;
    }
    public async Task<bool> IsCurrentAsync(Guid org,Guid user,Guid account,Guid revision,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var command=Command(db,"SELECT EXISTS(SELECT 1 FROM integrations.google_accounts WHERE organization_id=@org AND user_id=@user AND id=@id AND revision=@revision AND disconnected_at IS NULL AND NOT needs_reconnect AND protected_tokens IS NOT NULL)",("org",org),("user",user),("id",account),("revision",revision));return(bool)(await command.ExecuteScalarAsync(ct))!;
    }
    public async Task DisconnectAsync(Guid org,Guid user,Guid account,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct);await using var command=Command(db,"UPDATE integrations.google_accounts SET disconnected_at=COALESCE(disconnected_at,now()),protected_tokens=NULL,revision=@revision,refresh_lease=NULL,refresh_lease_until=NULL WHERE organization_id=@org AND user_id=@user AND id=@id",("org",org),("user",user),("id",account),("revision",Guid.NewGuid()));await command.ExecuteNonQueryAsync(ct);
    }
}
