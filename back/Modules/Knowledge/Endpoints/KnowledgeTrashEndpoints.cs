using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Knowledge.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Knowledge.Endpoints;

internal static class KnowledgeTrashEndpoints
{
 public static void MapKnowledgeTrash(this WebApplication app){
  var group=app.MapGroup("/api/organizations/{organizationId:guid}/knowledge-bases").RequireAuthorization();
  group.MapGet("/trash",async(Guid organizationId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();var cutoff=DateTime.UtcNow.AddDays(-30);
   return Results.Ok(await db.KnowledgeBases.IgnoreQueryFilters().Where(b=>b.OrganizationId==organizationId&&b.DeletedAt>cutoff).Select(b=>new{b.Id,b.Name,b.DeletedAt,purgeAt=b.DeletedAt!.Value.AddDays(30)}).ToListAsync(ct));
  });
  group.MapGet("/trash/documents",async(Guid organizationId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();var cutoff=DateTime.UtcNow.AddDays(-30);
   return Results.Ok(await (from d in db.Documents.IgnoreQueryFilters() join b in db.KnowledgeBases.IgnoreQueryFilters() on d.KnowledgeBaseId equals b.Id where d.OrganizationId==organizationId&&d.DeletedAt>cutoff select new{d.Id,name=d.OriginalFileName??d.Type,d.KnowledgeBaseId,d.DeletedAt,purgeAt=d.DeletedAt!.Value.AddDays(30),parentDeleted=b.DeletedAt!=null}).ToListAsync(ct));
  });
  group.MapDelete("/{knowledgeBaseId:guid}",async(Guid organizationId,Guid knowledgeBaseId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();await using var tx=await db.Database.BeginTransactionAsync(ct);
   var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.knowledge_bases SET deleted_at=now() WHERE organization_id={organizationId} AND id={knowledgeBaseId} AND deleted_at IS NULL",ct);if(count==0)return Results.NotFound();
   await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET processing_status='queued',lease_id=NULL,lease_until=NULL,processing_attempts=0,next_attempt_at=now() WHERE organization_id={organizationId} AND knowledge_base_id={knowledgeBaseId} AND processing_status='processing'",ct);await tx.CommitAsync(ct);return Results.NoContent();
  });
  group.MapPost("/{knowledgeBaseId:guid}/restore",async(Guid organizationId,Guid knowledgeBaseId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.knowledge_bases SET deleted_at=NULL WHERE organization_id={organizationId} AND id={knowledgeBaseId} AND deleted_at>now()-interval '30 days'",ct);return count==1?Results.NoContent():Results.NotFound();
  });
  group.MapDelete("/{knowledgeBaseId:guid}/documents/{documentId:guid}",async(Guid organizationId,Guid knowledgeBaseId,Guid documentId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct)||!await db.KnowledgeBases.AnyAsync(b=>b.Id==knowledgeBaseId&&b.OrganizationId==organizationId,ct))return Results.NotFound();
   var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET deleted_at=now(),processing_status=CASE WHEN processing_status='processing' THEN 'queued' ELSE processing_status END,lease_id=NULL,lease_until=NULL WHERE organization_id={organizationId} AND knowledge_base_id={knowledgeBaseId} AND id={documentId} AND deleted_at IS NULL",ct);return count==1?Results.NoContent():Results.NotFound();
  });
  group.MapPost("/{knowledgeBaseId:guid}/documents/{documentId:guid}/restore",async(Guid organizationId,Guid knowledgeBaseId,Guid documentId,IOrganizationAccess access,KnowledgeDbContext db,CancellationToken ct)=>{
   if(!await access.HasAccessAsync(organizationId,true,ct)||!await db.KnowledgeBases.AnyAsync(b=>b.Id==knowledgeBaseId&&b.OrganizationId==organizationId,ct))return Results.NotFound();
   var count=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET deleted_at=NULL WHERE organization_id={organizationId} AND knowledge_base_id={knowledgeBaseId} AND id={documentId} AND deleted_at>now()-interval '30 days'",ct);return count==1?Results.NoContent():Results.NotFound();
  });
 }
}
