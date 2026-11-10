using DocParse.Modules.Knowledge.Domain;

namespace DocParse.Modules.Knowledge.Application;

public interface IDocumentExtractor
{
    Task<string> ExtractDocumentDataAsync(string base64File, string mimeType, CancellationToken cancellationToken = default);
}

public interface IEmbeddingGenerator
{
    string Profile => "openai:text-embedding-3-small";
    async Task<IReadOnlyList<float[]>> GenerateManyAsync(IReadOnlyList<string> content, CancellationToken cancellationToken = default)
    {
        var result = new List<float[]>();
        foreach (var item in content) result.Add(await GenerateAsync(item, cancellationToken));
        return result;
    }
    Task<float[]> GenerateAsync(string content, CancellationToken cancellationToken = default);
}

public interface IOriginalFileStorage
{
    Task<string> UploadAsync(Guid documentId, Stream content, string extension, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default);
}

public interface IDocumentStore
{
    Task StageAsync(Guid documentId,CancellationToken ct);
    Task<Document?> FindDuplicateAsync(Guid org,Guid knowledgeBase,byte[] hash,CancellationToken ct);
    Task<Document> EnqueueAsync(Document document, CancellationToken cancellationToken);
    Task<Document?> ClaimAsync(Guid leaseId, CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid documentId, Guid leaseId, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Document document, IReadOnlyList<DocumentVector> vectors, Guid leaseId, CancellationToken cancellationToken);
    Task FailAsync(Guid documentId, Guid leaseId, string errorCode, bool retryable, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid documentId, Guid organizationId, Guid knowledgeBaseId, CancellationToken cancellationToken);
    Task<Document?> GetByIdAsync(string id, Guid organizationId, Guid knowledgeBaseId);
    Task<List<Document>> GetAllAsync(Guid organizationId, Guid knowledgeBaseId);
}

public interface IDocumentExporter
{
    byte[] ToCsv(Document document);
    byte[] ToExcel(Document document);
}

public sealed record DocumentVector(string Content, float[] Embedding, string Profile, int? PageNumber, int Index);
