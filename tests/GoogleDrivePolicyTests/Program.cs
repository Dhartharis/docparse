using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

var count=0;
void Check(bool value){if(!value)throw new Exception("Google Drive assertion failed.");count++;}
async Task Reject(Func<Task> action,string code){try{await action();}catch(GoogleDriveFailure error)when(error.Code==code){count++;return;}throw new Exception("Expected safe Google failure.");}
void Invalid(GoogleDriveOptions value){try{value.Validate(false,"https://docparse.example");}catch(InvalidOperationException){count++;return;}throw new Exception("Unsafe Google configuration accepted.");}
new GoogleDriveOptions().Validate(false,"");count++;
Invalid(new(){Enabled=true});
GoogleDriveOptions Options(string? token=null)=>new(){Enabled=true,ClientId="fixture-client",ClientSecret="fixture-secret",PickerApiKey="key",ProjectNumber="12345",RedirectUri="https://docparse.example/google-drive-callback.html",TokenEndpoint=token??"https://oauth2.googleapis.com/token"};
Options().Validate(false,"https://docparse.example");count++;
Invalid(Options("https://untrusted.example/token"));
using(var provider=new GoogleDriveProvider(Options(),TimeProvider.System))
{
    var url=provider.AuthorizationUrl("state&unsafe","challenge");
    Check(url.Contains("state=state%26unsafe")&&url.Contains("code_challenge_method=S256")&&url.Contains("access_type=offline")&&url.Contains("drive.file"));
}
if(args.Contains("--persistence"))
{
    var dbString=Environment.GetEnvironmentVariable("DOCPARSE_GOOGLE_TEST_DB")!;
    var org=Guid.Parse(Environment.GetEnvironmentVariable("DOCPARSE_GOOGLE_TEST_ORG")!);var user=Guid.Parse(Environment.GetEnvironmentVariable("DOCPARSE_GOOGLE_TEST_USER")!);
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"PostgreSQL:ConnectionString",dbString}}).Build();
    var protection=new EphemeralDataProtectionProvider();var clock=new Clock(DateTimeOffset.UtcNow);var store=new GoogleDriveAccounts(config,protection,clock);
    var id=Guid.NewGuid();var state=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await store.StartAsync(org,user,id,state,"verifier",default);
    var first=await store.ClaimAsync(org,user,state,default);Check(first.Verifier=="verifier"&&!first.Completed);
    await Reject(async()=>await store.ClaimAsync(org,user,state,default),"google_authorization");
    await store.CompleteAsync(org,user,state,id,new("fixture-access","fixture-refresh",clock.GetUtcNow().AddSeconds(3600),clock.GetUtcNow().AddDays(1)),default);
    Check((await store.ClaimAsync(org,user,state,default)).Completed);
    var fresh=await store.AcquireAsync(org,user,id,default);Check(fresh.LeaseId is null&&fresh.Tokens.AccessToken=="fixture-access");
    clock.Now=clock.Now.AddHours(1);
    var expired=await store.AcquireAsync(org,user,id,default);Check(expired.LeaseId is not null);
    await Reject(async()=>await store.AcquireAsync(org,user,id,default),"google_busy");
    using var provider=new GoogleDriveProvider(Options(Environment.GetEnvironmentVariable("DOCPARSE_GOOGLE_TEST_PROVIDER")+"/token"),clock);
    var renewed=await provider.RefreshAsync(expired.Tokens,default);Check(renewed.ExpiresAt==clock.Now.AddHours(1));
    Check(await store.SaveRefreshAsync(org,user,id,expired,renewed,default));
    var recreated=new GoogleDriveAccounts(config,protection,clock);Check((await recreated.AcquireAsync(org,user,id,default)).LeaseId is null);
    clock.Now=clock.Now.AddHours(1);var lease=await recreated.AcquireAsync(org,user,id,default);
    clock.Now=clock.Now.AddSeconds(31);
    await Reject(async()=>await recreated.AcquireAsync(org,user,id,default),"google_authorization");
    Check(!(await recreated.SaveRefreshAsync(org,user,id,lease,renewed,default)));
    Check((await recreated.ListAsync(org,user,default)).Single(x=>x.Id==id).NeedsReconnect);
    await recreated.DisconnectAsync(org,user,id,default);
    Check(!await recreated.IsCurrentAsync(org,user,id,lease.Revision,default));
    Check(!await recreated.SaveRefreshAsync(org,user,id,lease,renewed,default));
    await using var db=new NpgsqlConnection(dbString);await db.OpenAsync();
    await using var command=new NpgsqlCommand("DELETE FROM integrations.google_accounts WHERE id=@id",db);command.Parameters.AddWithValue("id",id);await command.ExecuteNonQueryAsync();
}
Console.WriteLine($"PASS: {count} Google Drive checks (configuration, OAuth encoding, durable state, refresh, expired leases, credential protection and disconnect fencing as enabled).");
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now{get;set;}=now;public override DateTimeOffset GetUtcNow()=>Now;}
