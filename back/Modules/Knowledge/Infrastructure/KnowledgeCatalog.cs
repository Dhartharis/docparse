using DocParse.Modules.Knowledge.Contracts;
using Microsoft.EntityFrameworkCore;
using DocParse.Modules.Knowledge.Application;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace DocParse.Modules.Knowledge.Infrastructure;

internal sealed class KnowledgeCatalog(KnowledgeDbContext database, IEmbeddingGenerator embeddings) : IKnowledgeCatalog
{
    public async Task<bool> ContainsAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, CancellationToken cancellationToken = default)
    {
        var ids = knowledgeBaseIds.Distinct().ToArray();
        return await database.KnowledgeBases.CountAsync(x => x.OrganizationId == organizationId && ids.Contains(x.Id), cancellationToken) == ids.Length;
    }

    public async Task<IReadOnlyList<KnowledgeSource>> SearchAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, string question, CancellationToken cancellationToken = default)
    {
        if (knowledgeBaseIds.Count == 0) return [];
        if (!await ContainsAsync(organizationId, knowledgeBaseIds, cancellationToken)) throw new InvalidOperationException("Invalid knowledge scope.");
        var vector = new Vector(await embeddings.GenerateAsync(question, cancellationToken));
        var ids = knowledgeBaseIds.ToArray();
        return await (from embedding in database.DocumentEmbeddings
            join document in database.Documents on embedding.DocumentId equals document.Id
            where document.ProcessingStatus == "ready" && document.OrganizationId == organizationId && document.KnowledgeBaseId != null && ids.Contains(document.KnowledgeBaseId.Value)
                && database.KnowledgeBases.Any(k=>k.Id==document.KnowledgeBaseId&&k.OrganizationId==organizationId)
                && embedding.EmbeddingProfile == embeddings.Profile
            orderby embedding.Embedding!.CosineDistance(vector)
            select new KnowledgeSource(document.Id, embedding.Content, document.OriginalFileName ?? document.Type, embedding.PageNumber))
            .Take(6).ToListAsync(cancellationToken);
    }
}
