using DocParse.Modules.Knowledge.Domain;
using DocParse.Modules.Knowledge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using DocParse.Modules.Knowledge.Application;
using Pgvector;
using Npgsql;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class DocumentRepository : IDocumentStore
{
    private readonly KnowledgeDbContext _dbContext;

    public DocumentRepository(KnowledgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task StageAsync(Guid documentId,CancellationToken ct)=>_dbContext.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO knowledge.file_cleanup(document_id) VALUES({documentId}) ON CONFLICT DO NOTHING",ct);
    public Task<Document?> FindDuplicateAsync(Guid org,Guid knowledgeBase,byte[] hash,CancellationToken ct)=>_dbContext.Documents.AsNoTracking().SingleOrDefaultAsync(d=>d.OrganizationId==org&&d.KnowledgeBaseId==knowledgeBase&&d.ContentHash==hash,ct);
    public async Task<Document> EnqueueAsync(Document document,CancellationToken ct) {
        await using var tx=await _dbContext.Database.BeginTransactionAsync(ct);
        try {
            _dbContext.Documents.Add(document);await _dbContext.SaveChangesAsync(ct);
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge.file_cleanup WHERE document_id={document.Id}",ct);
            await tx.CommitAsync(ct);return document;
        } catch(DbUpdateException e)when(e.InnerException is PostgresException{SqlState:"23505",ConstraintName:"document_active_content"}) {
            await tx.RollbackAsync(ct);_dbContext.Entry(document).State=EntityState.Detached;
            var existing=await FindDuplicateAsync(document.OrganizationId!.Value,document.KnowledgeBaseId!.Value,document.ContentHash!,ct);
            if(existing is null)throw;
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.file_cleanup SET due_at=now() WHERE document_id={document.Id}",ct);return existing;
        }
    }
    public async Task<Document?> ClaimAsync(Guid leaseId, CancellationToken ct)
    {
        // Each statement commits before extraction/provider I/O. SKIP LOCKED supports multiple workers.
        await _dbContext.Database.ExecuteSqlRawAsync("UPDATE knowledge.documents SET processing_status='failed', processing_error='interrupted', lease_id=NULL, lease_until=NULL, processing_updated_at=now() WHERE processing_status='processing' AND lease_until < now() AND processing_attempts >= 3", ct);
        var rows = await _dbContext.Documents.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT id FROM knowledge.documents
                WHERE deleted_at IS NULL AND EXISTS(SELECT 1 FROM knowledge.knowledge_bases b WHERE b.id=knowledge.documents.knowledge_base_id AND b.deleted_at IS NULL) AND processing_attempts < 3 AND ((processing_status='queued' AND next_attempt_at <= now()) OR (processing_status='processing' AND lease_until < now()))
                ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE knowledge.documents d SET processing_status='processing', processing_attempts=processing_attempts+1,
                lease_id={leaseId}, lease_until=now()+interval '2 minutes', processing_updated_at=now()
            FROM candidate c WHERE d.id=c.id RETURNING d.*
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }
    public async Task<bool> RenewAsync(Guid documentId, Guid leaseId, CancellationToken ct) =>
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET lease_until=now()+interval '2 minutes' WHERE deleted_at IS NULL AND id={documentId} AND lease_id={leaseId} AND processing_status='processing' AND lease_until > now()", ct) == 1;

    public async Task<bool> CompleteAsync(Document document, IReadOnlyList<DocumentVector> vectors, Guid leaseId, CancellationToken ct)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        var rows = await _dbContext.Documents.FromSqlInterpolated($"SELECT * FROM knowledge.documents WHERE id={document.Id} FOR UPDATE").ToListAsync(ct);
        var current = rows.SingleOrDefault();
        if (current is null || current.ProcessingStatus != "processing" || current.LeaseId != leaseId || current.LeaseUntil <= DateTime.UtcNow || !await _dbContext.KnowledgeBases.AnyAsync(b=>b.Id==current.KnowledgeBaseId,ct)) return false;
        current.Type = document.Type; current.Fields = document.Fields;
        current.ProcessingStatus = "ready"; current.ProcessingError = null;
        current.LeaseId = null; current.LeaseUntil = null; current.ProcessingUpdatedAt = DateTime.UtcNow;
        await _dbContext.DocumentEmbeddings.Where(x => x.DocumentId == document.Id).ExecuteDeleteAsync(ct);
        _dbContext.DocumentEmbeddings.AddRange(vectors.Select(item => new DocumentEmbedding { Id = Guid.NewGuid(), DocumentId = document.Id,
            Content = item.Content, Embedding = new Vector(item.Embedding), EmbeddingProfile = item.Profile,
            PageNumber = item.PageNumber, ChunkIndex = item.Index }));
        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    public async Task FailAsync(Guid documentId, Guid leaseId, string errorCode, bool retryable, CancellationToken ct)
    {
        // Retry times and attempt counts are durable, not in-memory timers.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE knowledge.documents SET processing_status=CASE WHEN {retryable} AND processing_attempts < 3 THEN 'queued' ELSE 'failed' END,
                next_attempt_at=now()+CASE WHEN processing_attempts=1 THEN interval '10 seconds' ELSE interval '30 seconds' END,
                processing_updated_at=now(), processing_error={errorCode}, lease_id=NULL, lease_until=NULL
            WHERE deleted_at IS NULL AND id={documentId} AND lease_id={leaseId} AND processing_status='processing' AND lease_until > now()
            """, ct);
    }
    public async Task<bool> RetryAsync(Guid documentId, Guid organizationId, Guid knowledgeBaseId, CancellationToken ct) =>
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge.documents SET processing_status='queued', processing_attempts=0, next_attempt_at=now(), processing_updated_at=now(), processing_error=NULL WHERE deleted_at IS NULL AND id={documentId} AND organization_id={organizationId} AND knowledge_base_id={knowledgeBaseId} AND processing_status='failed'", ct) == 1;

    public async Task<Document?> GetByIdAsync(string id, Guid organizationId, Guid knowledgeBaseId)
    {
        if (!Guid.TryParse(id, out var documentId))
            return null;

        return await _dbContext.Documents.AsNoTracking().SingleOrDefaultAsync(document => document.Id == documentId
            && document.OrganizationId == organizationId && document.KnowledgeBaseId == knowledgeBaseId);
    }

    public async Task<List<Document>> GetAllAsync(Guid organizationId, Guid knowledgeBaseId)
    {
        return await _dbContext.Documents.AsNoTracking().Where(document => document.OrganizationId == organizationId
            && document.KnowledgeBaseId == knowledgeBaseId).OrderByDescending(document => document.CreatedAt).ToListAsync();
    }
}
