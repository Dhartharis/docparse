using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs.Models;

namespace DocParse.Modules.Bots.Infrastructure;

public sealed class BotLogoStorage(BlobServiceClient blobs)
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient("bot-logos");
    public async Task UploadAsync(Guid botId, Guid version, Stream content, CancellationToken ct)
    {
        await Container.CreateIfNotExistsAsync(cancellationToken: ct);
        await Container.GetBlobClient($"{botId:N}/{version:N}.png").UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" } }, ct);
    }
    public async Task<Stream> DownloadAsync(Guid botId, Guid version, CancellationToken ct) =>
        (await Container.GetBlobClient($"{botId:N}/{version:N}.png").DownloadStreamingAsync(cancellationToken: ct)).Value.Content;
    public async Task<bool> IsReferencedAsync(BotsDbContext db,string key,CancellationToken ct){var parts=key.Split('/');if(!Guid.TryParseExact(parts[0],"N",out var bot))return true;if(parts.Length==2&&parts[1]=="")return await db.Bots.IgnoreQueryFilters().AnyAsync(b=>b.Id==bot,ct);if(parts.Length!=2||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(parts[1]),"N",out var version))return true;return await db.Bots.IgnoreQueryFilters().AnyAsync(b=>b.Id==bot&&b.LogoVersion==version,ct);}
    public async Task DeleteKeyAsync(string key,CancellationToken ct){if(!await Container.ExistsAsync(ct))return;if(key.EndsWith('/')){await foreach(var blob in Container.GetBlobsAsync(BlobTraits.None,BlobStates.None,prefix:key,cancellationToken:ct))await Container.DeleteBlobIfExistsAsync(blob.Name,cancellationToken:ct);}else await Container.DeleteBlobIfExistsAsync(key,cancellationToken:ct);}
    public async Task SweepOrphansAsync(BotsDbContext db,CancellationToken ct){
        if(!await Container.ExistsAsync(ct))return;
        var ids=await db.Bots.IgnoreQueryFilters().Select(b=>b.Id).ToArrayAsync(ct);
        foreach(var id in ids)await foreach(var blob in Container.GetBlobsAsync(BlobTraits.None,BlobStates.None,prefix:id.ToString("N")+"/",cancellationToken:ct)){
            if(blob.Properties.LastModified>DateTimeOffset.UtcNow.AddDays(-1)||await IsReferencedAsync(db,blob.Name,ct))continue;
            await Container.DeleteBlobIfExistsAsync(blob.Name,cancellationToken:ct);
        }
    }
}
