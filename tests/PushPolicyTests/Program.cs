using System.Security.Cryptography;
using System.Text;
using DocParse.Modules.Integrations.Infrastructure;
using DocParse.Modules.OperationalData.Application;
using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.OperationalData.Infrastructure;
using DocParse.Modules.Integrations.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

var now = DateTimeOffset.Parse("2026-11-11T12:00:00.999Z");
const string key = "provider-1", request = "aabbccdd-1122-3344-5566-778899aabbcc";
var secret = new string('a',64);
var count = 0;
void Check(bool condition) { if (!condition) throw new Exception("Policy assertion failed."); count++; }
void Reject(Action action) {
    try { action(); } catch (Exception error) when (error is PushRejected or OperationalDataInvalid) { count++; return; }
    throw new Exception("Expected policy rejection.");
}
string Sign(string method, string target, byte[] body, long seconds) {
    var canonical = string.Join("\n", "FYR-OPDATA-V1", method, target, key, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), request,
        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
    return "v1="+Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
var body = Encoding.UTF8.GetBytes("{\"value\":1}");
var seconds = now.ToUnixTimeSeconds();
const string target = "/api/events?z=%2F&a=1";
var signed = OperationalSignature.Parse(key,seconds.ToString(),request,Sign("POST",target,body,seconds),now);
signed.Verify(secret,"POST",target,body); count++;
Reject(() => signed.Verify(secret,"POST", "/api/events?a=1&z=%2F",body));
Reject(() => signed.Verify(secret,"GET",target,body));
Reject(() => signed.Verify(secret,"POST",target,Encoding.UTF8.GetBytes("{\"value\":1} ")));
foreach (var offset in new[] { -300, 300 }) {
    var value = seconds+offset;
    var boundary = OperationalSignature.Parse(key,value.ToString(),request,Sign("POST",target,body,value),now);
    boundary.Verify(secret,"POST",target,body); count++;
    Check(boundary.NonceExpiry.ToUnixTimeSeconds() == value+301);
}
foreach (var offset in new[] {-301,301}) Reject(() => OperationalSignature.Parse(key,(seconds+offset).ToString(),request,Sign("POST",target,body,seconds),now));
foreach (var invalid in new[] { "key\n", "has space", new string('a',129) }) Reject(() => OperationalSignature.Parse(invalid,seconds.ToString(),request,Sign("POST",target,body,seconds),now));
foreach (var invalid in new[] { "0"+seconds, seconds+"\n", "+"+seconds, "9999999999999" }) Reject(() => OperationalSignature.Parse(key,invalid,request,Sign("POST",target,body,seconds),now));
Reject(() => OperationalSignature.Parse(key,seconds.ToString(),request.ToUpperInvariant(),Sign("POST",target,body,seconds),now));
Reject(() => OperationalSignature.Parse(key,seconds.ToString(),request,Sign("POST",target,body,seconds)+"\n",now));
var quantity = new OperationalQuantity(true,null,"999999999999999999.123456");
var price = new OperationalPrice(true,null,"999999999999999999.123456","USD","inclusive",now,now.AddMinutes(1));
var stock = new OperationalAvailability("tracked",quantity,new(false,"not_provided",null),now,now.AddMinutes(1));
var offer = new OperationalOffer("offer-1","product-1",null,"Milk","One carton","L","99999999999999999999999999999999999999",now,true,price,stock);
void Validate(OperationalOffer value) => OperationalRules.Validate(new(value,null),now,0);
Validate(offer); count++;
foreach (var invalid in new[] { "-1", "1e3", "1.0000001", "01", "1000000000000000000", "1\n", ".5" })
    Reject(() => Validate(offer with {Price=price with {Amount=invalid}}));
foreach (var invalid in new[] { "0", "01", "1\n",new string('9',39) }) Reject(() => Validate(offer with {Version=invalid}));
Reject(() => Validate(offer with {ExternalId="id\n"}));
Reject(() => Validate(offer with {Price=price with {Currency="USD\n"}}));
Reject(() => Validate(offer with {Price=price with {ObservedAt=now.AddTicks(1)}}));
Reject(() => Validate(offer with {Availability=stock with {Tracking="untracked"}}));
Reject(() => Validate(offer with {Price=new(false,"not_provided","0",null,null,null,null)}));
Validate(offer with {Name=string.Concat(Enumerable.Repeat("😀",512))}); count++;
Reject(() => Validate(offer with {Name=string.Concat(Enumerable.Repeat("😀",513))}));
Reject(() => Validate(offer with {Name="milk\0"}));
OperationalRules.Validate(new(null,new("offer-1","3",now)),now,0); count++;
var payload = """
{"contractVersion":"1","sourceBusinessId":"business-1","eventId":"aabbccdd-1122-3344-5566-778899aabbcc","occurredAt":"2026-11-11T12:00:00Z","change":{"kind":"delete","tombstone":{"externalId":"offer-1","version":"2","deletedAt":"2026-11-11T12:00:00.1234567Z"}}}
""";
var parsed = OperationalEventCodec.Parse(Encoding.UTF8.GetBytes(payload));
Check(parsed.Change.Tombstone?.Version == "2" && parsed.Change.Tombstone.DeletedAt.Ticks % 10 == 7);
foreach (var invalid in new[] {
    payload.Replace("\"contractVersion\":\"1\"", "\"contractVersion\":\"1\",\"extra\":true"),
    payload.Replace("\"version\":\"2\"", "\"version\":2"),
    payload.Replace("\"version\":\"2\"", "\"version\":\"2\",\"version\":\"2\""),
    payload.Replace("business-1","business\\n"), payload.Replace(".1234567Z",".12345678Z"),
    payload.Replace(".1234567Z","+00:00"), payload.Replace("2026-11-11","2026-02-30"), "\ufeff"+payload
}) Reject(() => OperationalEventCodec.Parse(Encoding.UTF8.GetBytes(invalid)));
Reject(() => OperationalEventCodec.Parse([0xff,0xfe]));
Console.WriteLine($"PASS: {count} Push policy checks (raw HMAC target/body, timestamp boundaries, strict JSON, decimals, versions, unknowns, Unicode and UTC).");

if (args.Contains("--fencing"))
{
    // Only invoked by the disposable integration harness while its worker is stopped.
    var connectionString = Environment.GetEnvironmentVariable("DOCPARSE_PUSH_TEST_CONNECTION") ?? throw new Exception("Missing disposable test connection.");
    var org = Guid.Parse(Environment.GetEnvironmentVariable("DOCPARSE_PUSH_TEST_ORG")!);
    var connection = Guid.Parse(Environment.GetEnvironmentVariable("DOCPARSE_PUSH_TEST_SOURCE")!);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["PostgreSQL:ConnectionString"] = connectionString }).Build();
    var inbox = new PushInbox(configuration,new EphemeralDataProtectionProvider());
    var projection = new OperationalProjection(configuration);
    var receipt = Guid.NewGuid(); var currentLease = Guid.NewGuid(); var external = "fence-"+Guid.NewGuid().ToString("N");
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    async Task Execute(string text) { await using var command = new NpgsqlCommand(text,db); await command.ExecuteNonQueryAsync(); }
    async Task<string> Scalar(string text) { await using var command = new NpgsqlCommand(text,db); return (await command.ExecuteScalarAsync())!.ToString()!; }
    await Execute($"INSERT INTO integrations.push_inbox(id,organization_id,connection_id,event_id,body_hash,payload,state,attempts,lease_id,lease_until) VALUES('{receipt}','{org}','{connection}','{Guid.NewGuid()}',decode('aa','hex'),'{{}}','processing',1,'{currentLease}',now()+interval '1 minute')");
    var work = new PushWork(receipt,org,connection,Guid.NewGuid(),1,"{}",0);
    var change = new OperationalChange(null,new(external,"1",DateTimeOffset.UtcNow.AddSeconds(-1)));
    try
    {
        await inbox.ApplyAsync(work,change,projection,DateTimeOffset.UtcNow,CancellationToken.None);
        await inbox.FinishAsync(work,"failed","processing_failed",CancellationToken.None);
        await inbox.RetryAsync(work,CancellationToken.None);
        Check(await Scalar($"SELECT state||':'||lease_id FROM integrations.push_inbox WHERE id='{receipt}'") == "processing:"+currentLease);
        Check(await Scalar($"SELECT count(*) FROM operational_data.records WHERE external_id='{external}'") == "0");
        work = work with { LeaseId=currentLease };
        await inbox.ApplyAsync(work,change,projection,DateTimeOffset.UtcNow,CancellationToken.None);
        Check(await Scalar($"SELECT state FROM integrations.push_inbox WHERE id='{receipt}'") == "applied");
        Check(await Scalar($"SELECT version FROM operational_data.records WHERE external_id='{external}'") == "1");
        await Execute($"UPDATE integrations.push_inbox SET state='processing',lease_id='{currentLease}',lease_until=now()-interval '1 second' WHERE id='{receipt}'");
        await inbox.ApplyAsync(work,change with { Tombstone=change.Tombstone! with { Version="2" } },projection,DateTimeOffset.UtcNow,CancellationToken.None);
        await inbox.FinishAsync(work,"failed","processing_failed",CancellationToken.None);
        await inbox.RetryAsync(work,CancellationToken.None);
        Check(await Scalar($"SELECT state FROM integrations.push_inbox WHERE id='{receipt}'") == "processing");
        Check(await Scalar($"SELECT version FROM operational_data.records WHERE external_id='{external}'") == "1");
        Console.WriteLine("PASS: 6 persistence checks: obsolete and expired workers cannot apply, finish or retry; the current lease commits both projection and receipt.");
    }
    finally
    {
        await Execute($"DELETE FROM integrations.push_inbox WHERE id='{receipt}'; DELETE FROM operational_data.records WHERE organization_id='{org}' AND connection_id='{connection}' AND external_id='{external}'");
    }
}
