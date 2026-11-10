using DocParse.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots.Endpoints;

internal static class BotTrashEndpoints
{
 public static void MapBotTrash(this WebApplication app){
  var group=app.MapGroup("/api/organizations/{organizationId:guid}/bots").RequireAuthorization();
  group.MapGet("/trash",async(Guid organizationId,IOrganizationAccess access,BotsDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();var cutoff=DateTime.UtcNow.AddDays(-30);
   return Results.Ok(await db.Bots.IgnoreQueryFilters().Where(b=>b.OrganizationId==organizationId&&b.DeletedAt>cutoff).Select(b=>new{b.Id,b.Name,b.DeletedAt,purgeAt=b.DeletedAt!.Value.AddDays(30)}).ToListAsync(ct));
  });
  group.MapDelete("/{botId:guid}",async(Guid organizationId,Guid botId,IOrganizationAccess access,BotsDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();await using var tx=await db.Database.BeginTransactionAsync(ct);
   var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bots.bots SET deleted_at=now(),embed_enabled=false,embed_version=gen_random_uuid() WHERE organization_id={organizationId} AND id={botId} AND deleted_at IS NULL",ct);
   if(count==0)return Results.NotFound();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bots.api_keys SET revoked_at=COALESCE(revoked_at,now()) WHERE organization_id={organizationId} AND bot_id={botId}",ct);
   await tx.CommitAsync(ct);return Results.NoContent();
  });
  group.MapPost("/{botId:guid}/restore",async(Guid organizationId,Guid botId,IOrganizationAccess access,BotsDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bots.bots SET deleted_at=NULL WHERE organization_id={organizationId} AND id={botId} AND deleted_at>now()-interval '30 days'",ct);return count==1?Results.NoContent():Results.NotFound();
  });
 }
}
