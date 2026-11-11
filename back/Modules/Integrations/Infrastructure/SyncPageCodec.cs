using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.OperationalData.Contracts;
using static DocParse.Modules.Integrations.Infrastructure.OperationalEventCodec;

namespace DocParse.Modules.Integrations.Infrastructure;

public static class SyncPageCodec
{
    public static OperationalPage Parse(byte[] bytes, string business, IOperationalProjection projection, DateTimeOffset now, int skew)
    {
        try
        {
            _=new UTF8Encoding(false,true).GetString(bytes);
            using var document=JsonDocument.Parse(bytes); var root=document.RootElement;
            Shape(root,"contractVersion","sourceBusinessId","mode","snapshotId","snapshotWatermark","complete","nextCursor","changes");
            if(Text(root,"contractVersion")!="1" || Text(root,"sourceBusinessId")!=business) throw Invalid();
            var mode=Text(root,"mode"); var cursor=Text(root,"nextCursor");
            if(mode is not("snapshot" or "incremental") || cursor.Length==0 || cursor.EnumerateRunes().Count()>2048 || cursor.Contains('\0')) throw Invalid();
            string? snapshot=null,watermark=null;
            if(mode=="snapshot")
            {
                snapshot=Text(root,"snapshotId");watermark=Text(root,"snapshotWatermark");
                if(snapshot.Length==0 || snapshot.EnumerateRunes().Count()>256 || snapshot.Contains('\0') || !Regex.IsMatch(watermark,@"\A[1-9][0-9]{0,37}\z")) throw Invalid();
            }
            else if(root.GetProperty("snapshotId").ValueKind!=JsonValueKind.Null || root.GetProperty("snapshotWatermark").ValueKind!=JsonValueKind.Null) throw Invalid();
            var complete=root.GetProperty("complete");var changes=root.GetProperty("changes");
            if(complete.ValueKind is not(JsonValueKind.True or JsonValueKind.False) || changes.ValueKind!=JsonValueKind.Array || changes.GetArrayLength()>100) throw Invalid();
            var parsed=new List<OperationalChange>();
            foreach(var change in changes.EnumerateArray())
            {
                OperationalChange item;
                if(Text(change,"kind")=="upsert") {Shape(change,"kind","offer");item=new(ReadOffer(change.GetProperty("offer")),null);}
                else
                {
                    if(mode=="snapshot" || Text(change,"kind")!="delete") throw Invalid();
                    Shape(change,"kind","tombstone"); var value=change.GetProperty("tombstone"); Shape(value,"externalId","version","deletedAt");
                    item=new(null,new(Text(value,"externalId"),Text(value,"version"),Instant(value,"deletedAt")));
                }
                projection.Validate(item,now,skew);parsed.Add(item);
            }
            return new(mode,snapshot,watermark,complete.GetBoolean(),cursor,parsed);
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException or PushRejected or OperationalDataInvalid)
        {throw Invalid();}
    }
    private static LiveQueryFailure Invalid()=>new("provider_response");
}
