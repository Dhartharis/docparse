using System.Security.Cryptography;
using DocParse.Modules.Knowledge.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocParse.Modules.Knowledge.Infrastructure;

internal sealed class KnowledgeRetentionWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<KnowledgeRetentionWorker> logger):BackgroundService
{
 private async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(config["PostgreSQL:ConnectionString"]);await db.OpenAsync(ct);return db;}
 protected override async Task ExecuteAsync(CancellationToken ct){while(!ct.IsCancellationRequested){try{
  await using var scope=scopes.CreateAsyncScope();var context=scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();var files=scope.ServiceProvider.GetRequiredService<DocumentFileStorage>();
  await Purge(ct);await Cleanup(context,files,ct);await Backfill(context,files,ct);
  await Task.Delay(1000,ct);
 }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception e){logger.LogWarning("Knowledge retention iteration failed: {FailureType}",e.GetType().Name);await Task.Delay(5000,ct);}}}
 private async Task Purge(CancellationToken ct){await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var select=new NpgsqlCommand("SELECT d.id FROM knowledge.documents d JOIN knowledge.knowledge_bases b ON b.id=d.knowledge_base_id WHERE d.deleted_at<=now()-interval '30 days' OR b.deleted_at<=now()-interval '30 days' ORDER BY d.created_at FOR UPDATE OF d SKIP LOCKED LIMIT 1",db);var id=await select.ExecuteScalarAsync(ct);
  if(id is Guid doc){await using var add=new NpgsqlCommand("INSERT INTO knowledge.file_cleanup(document_id,due_at) VALUES(@id,now()) ON CONFLICT(document_id) DO UPDATE SET due_at=now(); DELETE FROM knowledge.documents WHERE id=@id",db);add.Parameters.AddWithValue("id",doc);await add.ExecuteNonQueryAsync(ct);}
  await using var bases=new NpgsqlCommand("DELETE FROM knowledge.knowledge_bases WHERE id=(SELECT b.id FROM knowledge.knowledge_bases b WHERE b.deleted_at<=now()-interval '30 days' AND NOT EXISTS(SELECT 1 FROM knowledge.documents d WHERE d.knowledge_base_id=b.id) ORDER BY b.deleted_at FOR UPDATE SKIP LOCKED LIMIT 1)",db);await bases.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
 }
 private async Task Cleanup(KnowledgeDbContext context,DocumentFileStorage files,CancellationToken ct){Guid id;var lease=Guid.NewGuid();await using(var db=await Open(ct)){
  await using var claim=new NpgsqlCommand("UPDATE knowledge.file_cleanup SET lease_id=@lease,lease_until=now()+interval '2 minutes',attempts=attempts+1 WHERE document_id=(SELECT document_id FROM knowledge.file_cleanup WHERE due_at<=now() AND(lease_until IS NULL OR lease_until<now()) ORDER BY due_at FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING document_id",db);claim.Parameters.AddWithValue("lease",lease);var value=await claim.ExecuteScalarAsync(ct);if(value is not Guid document)return;id=document;
 }
 try{if(!await context.Documents.IgnoreQueryFilters().AnyAsync(d=>d.Id==id,ct))await files.DeletePrefixAsync(id,ct);await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge.file_cleanup WHERE document_id={id} AND lease_id={lease}",ct);}
 catch{await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.file_cleanup SET lease_id=NULL,lease_until=NULL,due_at=now()+make_interval(secs=>LEAST(300,5*attempts)) WHERE document_id={id} AND lease_id={lease}",ct);throw;}
 }
 private static async Task Backfill(KnowledgeDbContext context,DocumentFileStorage files,CancellationToken ct){
  var rows=await context.Documents.FromSqlRaw("UPDATE knowledge.documents SET hash_retry_at=now()+interval '10 minutes' WHERE id=(SELECT id FROM knowledge.documents WHERE content_hash IS NULL AND original_blob_name IS NOT NULL AND hash_retry_at<=now() ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING *").IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);var document=rows.SingleOrDefault();if(document is null)return;
  try{await using var stream=await files.DownloadAsync(document.OriginalBlobName!,ct);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long length=0;int count;while((count=await stream.ReadAsync(buffer,ct))>0){length+=count;if(length>100*1024*1024)throw new InvalidDataException("Legacy file exceeds hashing limit.");hash.AppendData(buffer,0,count);}var digest=hash.GetHashAndReset();await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET content_hash={digest},original_size_bytes={length} WHERE id={document.Id}",ct);}
  catch{await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET hash_retry_at=now()+interval '1 day' WHERE id={document.Id}",ct);throw;}
 }
}
