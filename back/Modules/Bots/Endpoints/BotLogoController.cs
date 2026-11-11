using System.Buffers.Binary;
using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Bots.Contracts;
using DocParse.Modules.Bots.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots.Endpoints;

[ApiController, Authorize]
[Route("api/organizations/{organizationId:guid}/bots/{botId:guid}/logo")]
public sealed class BotLogoController(IOrganizationAccess access, IBotCatalog catalog, BotsDbContext db, BotLogoStorage storage) : ControllerBase
{
    [HttpPost, RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid organizationId, Guid botId, IFormFile file, CancellationToken ct)
    {
        if (!await access.HasAccessAsync(organizationId, true, ct)) return NotFound();
        var bot = await db.Bots.SingleOrDefaultAsync(x => x.Id == botId && x.OrganizationId == organizationId, ct);
        if (bot is null) return NotFound();
        if (file is null || file.Length < 33 || file.Length > 1024 * 1024) return BadRequest();
        await using var content = new MemoryStream();
        await file.CopyToAsync(content, ct);
        if (!IsValidPng(content.GetBuffer(), (int)content.Length)) return BadRequest();
        var version = Guid.NewGuid(); content.Position = 0;
        var key=botId.ToString("N")+"/"+version.ToString("N")+".png";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO bots.logo_cleanup(blob_key) VALUES({key})",ct);
        await storage.UploadAsync(botId, version, content, ct);
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        var current=await db.Bots.FromSqlInterpolated($"SELECT * FROM bots.bots WHERE organization_id={organizationId} AND id={botId} AND deleted_at IS NULL FOR UPDATE").IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
        if(current is null)return NotFound();
        if(current.LogoVersion is Guid previous){var oldKey=botId.ToString("N")+"/"+previous.ToString("N")+".png";await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO bots.logo_cleanup(blob_key,due_at) VALUES({oldKey},now()) ON CONFLICT DO NOTHING",ct);}
        bot.LogoVersion = version;await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bots.logo_cleanup WHERE blob_key={key}",ct);await transaction.CommitAsync(ct);
        return Ok(new { LogoVersion = version });
    }
    private static bool IsValidPng(byte[] buffer, int length)
    {
        var bytes = buffer.AsSpan(0, length);
        // Bound image dimensions and reject non-PNG headers before private Blob storage.
        if (!bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(8, 4)) != 13
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (width is < 1 or > 1024 || height is < 1 or > 1024) return false;
        var offset = 8;
        var hasData = false;
        while (offset <= length - 12)
        {
            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
            if (chunkLength < 0 || chunkLength > length - offset - 12) return false;
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8) && chunkLength > 0) hasData = true;
            if (type.SequenceEqual("IEND"u8)) return hasData && chunkLength == 0 && offset + 12 == length;
            offset += chunkLength + 12;
        }
        return false;
    }
    [HttpGet]
    public async Task<IActionResult> Get(Guid organizationId, Guid botId, CancellationToken ct)
    {
        if (await catalog.GetForCurrentUserAsync(organizationId, botId, ct) is null) return NotFound();
        var version = await db.Bots.Where(x => x.Id == botId && x.OrganizationId == organizationId).Select(x => x.LogoVersion).SingleAsync(ct);
        if (version is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(await storage.DownloadAsync(botId, version.Value, ct), "image/png");
    }
}
