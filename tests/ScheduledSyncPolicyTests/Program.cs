using System.Text;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;
using DocParse.Modules.OperationalData.Infrastructure;
using Microsoft.Extensions.Configuration;

var count=0;
void Check(bool value){if(!value)throw new Exception("Synchronization policy assertion failed.");count++;}
void Reject(Action action){try{action();}catch(LiveQueryFailure){count++;return;}throw new Exception("Expected invalid page rejection.");}
var delays=new[]{10,30,120,300};
for(var attempt=1;attempt<=4;attempt++)
{
    var action=SyncRetryPolicy.Decide(attempt,true,null,false,true,300);
    Check(action.Retry&&!action.Restart&&action.DelaySeconds==delays[attempt-1]);
    Check(SyncRetryPolicy.Decide(attempt,true,3600,false,true,300).DelaySeconds==3600);
    Check(SyncRetryPolicy.Decide(attempt,true,1,false,true,300).DelaySeconds==delays[attempt-1]);
    foreach(var invalid in new[]{-1,0,3601})Check(SyncRetryPolicy.Decide(attempt,true,invalid,false,true,300).DelaySeconds==delays[attempt-1]);
    Check(!SyncRetryPolicy.Decide(attempt,false,3600,false,true,300).Retry);
}
Check(!SyncRetryPolicy.Decide(5,true,null,false,true,300).Retry);
Check(SyncRetryPolicy.Decide(5,true,null,true,true,300)==new SyncFailureAction(true,false,300));
Check(SyncRetryPolicy.Decide(1,false,null,true,false,300)==new SyncFailureAction(false,false,0));
var now=DateTimeOffset.Parse("2026-11-11T12:00:00Z");
var projection=new OperationalProjection(new ConfigurationBuilder().Build());
const string json="""
{"contractVersion":"1","sourceBusinessId":"biz","mode":"snapshot","snapshotId":"snapshot-1","snapshotWatermark":"99999999999999999999999999999999999999","complete":true,"nextCursor":"opaque/+secret","changes":[]}
""";
var parsed=SyncPageCodec.Parse(Encoding.UTF8.GetBytes(json),"biz",projection,now,0);
Check(parsed.Complete&&parsed.Changes.Count==0&&parsed.NextCursor=="opaque/+secret");
var incremental=json.Replace("\"mode\":\"snapshot\"","\"mode\":\"incremental\"").Replace("\"snapshotId\":\"snapshot-1\"","\"snapshotId\":null").Replace("\"snapshotWatermark\":\"99999999999999999999999999999999999999\"","\"snapshotWatermark\":null");
Check(SyncPageCodec.Parse(Encoding.UTF8.GetBytes(incremental),"biz",projection,now,0).Mode=="incremental");
foreach(var invalid in new[]{json.Replace("\"sourceBusinessId\":\"biz\"","\"sourceBusinessId\":\"other\""),json.Replace("\"complete\":true","\"complete\":true,\"complete\":true"),json.Replace("\"snapshot-1\"","null"),json.Replace("\"opaque/+secret\"","\"\""),json.Replace("\"changes\":[]","\"extra\":false,\"changes\":[]"),incremental.Replace("\"snapshotId\":null","\"snapshotId\":\"wrong\"")})Reject(()=>SyncPageCodec.Parse(Encoding.UTF8.GetBytes(invalid),"biz",projection,now,0));
var deletion="""{"kind":"delete","tombstone":{"externalId":"x","version":"2","deletedAt":"2026-11-11T12:00:00Z"}}""";
Reject(()=>SyncPageCodec.Parse(Encoding.UTF8.GetBytes(json.Replace("[]","["+deletion+"]")),"biz",projection,now,0));
Reject(()=>SyncPageCodec.Parse(Encoding.UTF8.GetBytes(incremental.Replace("[]","["+string.Join(',',Enumerable.Repeat(deletion,101))+"]")),"biz",projection,now,0));
Console.WriteLine($"PASS: {count} synchronization policy checks: exact retries, Retry-After bounds, exhaustion/cursor recovery, strict snapshot/incremental envelopes, limits and exact watermark/cursor handling.");
