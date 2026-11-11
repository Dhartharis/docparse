using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocParse.Modules.Bots.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DocParse.Modules.Bots.Infrastructure;

public sealed record ApiKeyInfo(Guid Id, string Name, DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt);
public sealed record IssuedApiKey(ApiKeyInfo Key, string Secret);
public sealed record EmbedSettings(bool Enabled, string[] Origins);
public sealed class ChannelRateExceeded : Exception;

public sealed class BotChannelStore(IConfiguration configuration, IDataProtectionProvider protection) : IBotChannelAccess
{
    private readonly IDataProtector sessions = protection.CreateProtector("DocParse.Widget.Session.v1");
    private async Task<NpgsqlConnection> Open(CancellationToken ct) { var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]); await db.OpenAsync(ct); return db; }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params (string Name, object Value)[] args) {
        var cmd = new NpgsqlCommand(sql, db); foreach(var (name, value) in args) cmd.Parameters.AddWithValue(name,value); return cmd;
    }
    public async Task<ApiKeyInfo[]> ListAsync(Guid organizationId, Guid botId, CancellationToken ct) {
        await using var db = await Open(ct); await using var cmd = Command(db,"SELECT id,name,created_at,expires_at,revoked_at,last_used_at FROM bots.api_keys WHERE organization_id=@org AND bot_id=@bot ORDER BY created_at DESC",("org",organizationId),("bot",botId));
        await using var r=await cmd.ExecuteReaderAsync(ct); var result=new List<ApiKeyInfo>(); while(await r.ReadAsync(ct)) result.Add(new(r.GetGuid(0),r.GetString(1),r.GetDateTime(2),r.GetDateTime(3),r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetDateTime(5))); return result.ToArray();
    }
    public async Task<IssuedApiKey?> IssueAsync(Guid organizationId, Guid botId, string name, int days, Guid? rotate, CancellationToken ct) {
        if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>100||days is <1 or >365) throw new ArgumentException();
        var id=Guid.NewGuid(); var secret="dp_"+id.ToString("N")+"."+Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now=DateTime.UtcNow; var expires=now.AddDays(days); await using var db=await Open(ct); await using var tx=await db.BeginTransactionAsync(ct);
        await using(var exists=Command(db,"SELECT id FROM bots.bots WHERE organization_id=@org AND id=@bot AND deleted_at IS NULL FOR UPDATE",("org",organizationId),("bot",botId))){if(await exists.ExecuteScalarAsync(ct)is null)return null;}
        if(rotate is not null) {
            await using var revoke=Command(db,"UPDATE bots.api_keys SET revoked_at=now() WHERE id=@id AND organization_id=@org AND bot_id=@bot AND revoked_at IS NULL",("id",rotate.Value),("org",organizationId),("bot",botId));
            if(await revoke.ExecuteNonQueryAsync(ct)!=1)return null;
        }
        await using var add=Command(db,"INSERT INTO bots.api_keys(id,organization_id,bot_id,name,secret_hash,created_at,expires_at) VALUES(@id,@org,@bot,@name,@hash,@now,@expires)",("id",id),("org",organizationId),("bot",botId),("name",name.Trim()),("hash",SHA256.HashData(Encoding.UTF8.GetBytes(secret))),("now",now),("expires",expires));
        await add.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return new(new(id,name.Trim(),now,expires,null,null),secret);
    }
    public async Task RevokeAsync(Guid organizationId, Guid botId, Guid id, CancellationToken ct) {
        await using var db=await Open(ct); await using var cmd=Command(db,"UPDATE bots.api_keys SET revoked_at=COALESCE(revoked_at,now()) WHERE id=@id AND organization_id=@org AND bot_id=@bot",("id",id),("org",organizationId),("bot",botId));await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<EmbedSettings?> SettingsAsync(Guid org,Guid bot,CancellationToken ct) {
        await using var db=await Open(ct); await using var cmd=Command(db,"SELECT embed_enabled,embed_origins FROM bots.bots WHERE organization_id=@org AND id=@bot AND deleted_at IS NULL",("org",org),("bot",bot));await using var r=await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct)?new(r.GetBoolean(0),r.GetFieldValue<string[]>(1)):null;
    }
    public async Task ConfigureAsync(Guid org,Guid bot,EmbedSettings settings,CancellationToken ct) {
        if(settings.Origins is null||settings.Origins.Length>20) throw new ArgumentException();
        var origins=settings.Origins.Select(value=> {
            if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme is not("http" or "https")||u.UserInfo!=""||u.PathAndQuery!="/"||u.Fragment!="")throw new ArgumentException();
            if(u.Scheme=="http"&&!u.IsLoopback) throw new ArgumentException();
            return u.GetLeftPart(UriPartial.Authority);
        }).Distinct().ToArray();
        if(settings.Enabled&&origins.Length==0)throw new ArgumentException();
        await using var db=await Open(ct);await using var cmd=Command(db,"UPDATE bots.bots SET embed_enabled=@enabled,embed_origins=@origins,embed_version=gen_random_uuid() WHERE organization_id=@org AND id=@bot AND deleted_at IS NULL",("org",org),("bot",bot),("enabled",settings.Enabled),("origins",origins));await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<ChannelBot?> ReadBot(NpgsqlConnection db,Guid bot,Guid? org,CancellationToken ct) {
        await using var cmd=Command(db,"SELECT organization_id,name,welcome_message,accent_color,logo_version FROM bots.bots WHERE id=@bot AND deleted_at IS NULL",("bot",bot));
        Guid organization;PublicBot branding;
        await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return null;organization=r.GetGuid(0);if(org is not null&&org!=organization)return null;branding=new(bot,r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetGuid(4));}
        await using var bases=Command(db,"SELECT knowledge_base_id FROM bots.bot_knowledge_bases WHERE organization_id=@org AND bot_id=@bot",("org",organization),("bot",bot));var ids=new List<Guid>();await using(var r=await bases.ExecuteReaderAsync(ct)){while(await r.ReadAsync(ct))ids.Add(r.GetGuid(0));}
        return new(new(bot,organization,ids),branding);
    }
    private async Task Admit(NpgsqlConnection db,Guid org,Guid bot,string subject,int limit,CancellationToken ct) {
        await using var cmd=Command(db,"INSERT INTO bots.channel_windows(organization_id,bot_id,subject,window_start,count) VALUES(@org,@bot,@subject,date_trunc('minute',now()),1) ON CONFLICT(organization_id,bot_id,subject,window_start) DO UPDATE SET count=channel_windows.count+1 WHERE channel_windows.count<@limit RETURNING count",("org",org),("bot",bot),("subject",subject),("limit",limit));
        if(await cmd.ExecuteScalarAsync(ct) is null)throw new ChannelRateExceeded();
        await using var cleanup=Command(db,"DELETE FROM bots.channel_windows WHERE organization_id=@org AND bot_id=@bot AND window_start<now()-interval '1 hour'",("org",org),("bot",bot));await cleanup.ExecuteNonQueryAsync(ct);
    }
    public async Task<ChannelBot?> AuthenticateKeyAsync(Guid organizationId,Guid botId,string credential,CancellationToken ct) {
        if(credential.Length!=100||!credential.StartsWith("dp_")||!Guid.TryParseExact(credential.Substring(3,32),"N",out var id))return null;
        await using var db=await Open(ct);byte[] hash;
        await using(var cmd=Command(db,"SELECT secret_hash FROM bots.api_keys WHERE id=@id AND organization_id=@org AND bot_id=@bot AND revoked_at IS NULL AND expires_at>now()",("id",id),("org",organizationId),("bot",botId))){hash=(await cmd.ExecuteScalarAsync(ct) as byte[])??[];}
        if(!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(Encoding.UTF8.GetBytes(credential))))return null;
        var bot=await ReadBot(db,botId,organizationId,ct);if(bot is null)return null;
        await Admit(db,organizationId,botId,"key:"+id,20,ct);await Admit(db,organizationId,botId,"all",120,ct);
        await using var used=Command(db,"UPDATE bots.api_keys SET last_used_at=now() WHERE id=@id",("id",id));await used.ExecuteNonQueryAsync(ct);return bot;
    }
    public async Task<(ChannelBot Bot,string Token,string[] Origins)?> CreateSessionAsync(Guid botId,string parentOrigin,CancellationToken ct) {
        await using var db=await Open(ct);Guid version;string[] origins;
        await using(var cmd=Command(db,"SELECT embed_version,embed_origins FROM bots.bots WHERE id=@bot AND deleted_at IS NULL AND embed_enabled",("bot",botId)))await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return null;version=r.GetGuid(0);origins=r.GetFieldValue<string[]>(1);}
        if(!origins.Contains(parentOrigin,StringComparer.OrdinalIgnoreCase))return null;
        var bot=await ReadBot(db,botId,null,ct);if(bot is null)return null;
        await Admit(db,bot.Bot.OrganizationId,botId,"sessions",60,ct);
        var token=sessions.Protect(JsonSerializer.Serialize(new WidgetSession(botId,version,Guid.NewGuid(),DateTime.UtcNow.AddMinutes(30))));
        return (bot,token,origins);
    }
    public async Task<ChannelBot?> AuthenticateEmbedAsync(Guid botId,string credential,CancellationToken ct) {
        if(string.IsNullOrEmpty(credential)||credential.Length>2000)return null;WidgetSession? session;
        try{session=JsonSerializer.Deserialize<WidgetSession>(sessions.Unprotect(credential));}catch(Exception e)when(e is CryptographicException or JsonException){return null;}
        if(session is null||session.Bot!=botId||session.Expires<=DateTime.UtcNow)return null;
        await using var db=await Open(ct);await using(var cmd=Command(db,"SELECT 1 FROM bots.bots WHERE id=@bot AND deleted_at IS NULL AND embed_enabled AND embed_version=@version",("bot",botId),("version",session.Version))){if(await cmd.ExecuteScalarAsync(ct)is null)return null;}
        var bot=await ReadBot(db,botId,null,ct);if(bot is null)return null;await Admit(db,bot.Bot.OrganizationId,botId,"session:"+session.Id,10,ct);await Admit(db,bot.Bot.OrganizationId,botId,"all",120,ct);return bot;
    }
    private sealed record WidgetSession(Guid Bot,Guid Version,Guid Id,DateTime Expires);
}
