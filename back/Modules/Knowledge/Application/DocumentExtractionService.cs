using System.Text.Json;
using DocParse.Modules.Knowledge.Contracts;
using DocParse.Modules.Usage.Contracts;
using System.Text;
using System.Security.Cryptography;
using DocParse.Modules.Knowledge.Domain;

namespace DocParse.Modules.Knowledge.Application;

public sealed class DocumentExtractionService(IDocumentExtractor extractor, IDocumentStore documents,
    IEmbeddingGenerator embeddings, IOriginalFileStorage files,IUsageMeter usage)
{
    public async Task<DocumentDTO> ExtractAsync(Stream input, string fileName, Guid organizationId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = extension switch
        {
            ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            _ => throw new DocumentUploadRejected("Unsupported file type.")
        };
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 10 * 1024 * 1024) throw new DocumentUploadRejected("Maximum file size is 10 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
        if (buffer.Length == 0) throw new DocumentUploadRejected("The document is empty.");
        var hash=SHA256.HashData(buffer.GetBuffer().AsSpan(0,(int)buffer.Length));
        var duplicate=await documents.FindDuplicateAsync(organizationId,knowledgeBaseId,hash,cancellationToken);
        if(duplicate is not null)return new DocumentDTO{Id=duplicate.Id.ToString(),ProcessingStatus=duplicate.ProcessingStatus,Deduplicated=true};
        var document = new Document {
            OrganizationId = organizationId, KnowledgeBaseId = knowledgeBaseId, Type = "Unknown",
            ContentHash=hash,OriginalSizeBytes=buffer.Length,OriginalFileName = Path.GetFileName(fileName), OriginalContentType = contentType, ProcessingStatus = "queued"
        };
        buffer.Position = 0;
        await documents.StageAsync(document.Id,cancellationToken);
        document.OriginalBlobName = await files.UploadAsync(document.Id, buffer, extension, contentType, cancellationToken);
        var accepted=await documents.EnqueueAsync(document,cancellationToken);
        return new DocumentDTO { Id = accepted.Id.ToString(), ProcessingStatus = accepted.ProcessingStatus,Deduplicated=accepted.Id!=document.Id };
    }

    public async Task ProcessAsync(Document document, Guid leaseId, CancellationToken cancellationToken)
    {
        usage.ValidateProcessingProvider();
        await using var input = await files.DownloadAsync(document.OriginalBlobName!, cancellationToken);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken);
        var raw = await extractor.ExtractDocumentDataAsync(Convert.ToBase64String(buffer.ToArray()), document.OriginalContentType!, cancellationToken);
        var dto = ParseResponse(raw);
        var content = $"Type: {dto.DocumentType}. Fields: {string.Join(", ", dto.Fields.Select(field => $"{field.Key}: {field.Value}"))}";
        var chunks = dto.Pages.Count > 0 ? dto.Pages.SelectMany(page => Split(page.Text).Select(text => (Text: text, Page: (int?)page.Number))).ToArray()
            : new[] { (Text: content, Page: (int?)null) };
        if (chunks.Length == 0 || chunks.Length > 1024) throw new DocumentUploadRejected("The document exceeds processing limits or has no readable text.");
        var operation=await usage.StartAsync(document.OrganizationId!.Value,null,"ingestion","worker",chunks.Sum(chunk=>(long)Encoding.UTF8.GetByteCount(chunk.Text))+chunks.Length*100,0,0,cancellationToken);
        try {
        var generated = await embeddings.GenerateManyAsync(chunks.Select(x => x.Text).ToArray(), cancellationToken);
        if (generated.Count != chunks.Length) throw new InvalidDataException("Embedding count mismatch.");
        var vectors = chunks.Select((chunk, index) => new DocumentVector(chunk.Text, generated[index], embeddings.Profile, chunk.Page, index)).ToArray();
        document.Type = dto.DocumentType; document.Fields = dto.Fields;
        var committed=await documents.CompleteAsync(document, vectors, leaseId, cancellationToken);
        await operation.CompleteAsync(committed,CancellationToken.None);
        } catch { await operation.CompleteAsync(false,CancellationToken.None); throw; }

    }

    private static IEnumerable<string> Split(string text)
    {
        // Overlap preserves context across chunk boundaries without truncating pages.
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(1800, text.Length - start);
            var chunk = text.Substring(start, length).Trim();
            if (chunk.Length > 0) yield return chunk;
            if (start + length >= text.Length) break;
            start += length - 200;
        }
    }

    private static DocumentDTO ParseResponse(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw.Replace("```json", "").Replace("```", "").Trim());
            var root = json.RootElement;
            var result = new DocumentDTO { DocumentType = root.GetProperty("type").GetString() ?? "Unknown" };
            foreach (var field in root.GetProperty("fields").EnumerateObject()) result.Fields[field.Name] = field.Value.ToString();
            if (root.TryGetProperty("pages", out var pages)) result.Pages = pages.EnumerateArray()
                .Select(page => new DocumentPage(page.GetProperty("number").GetInt32(), page.GetProperty("text").GetString() ?? "")).ToArray();
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Document extraction returned an invalid response.", exception);
        }
    }
}

public sealed class DocumentUploadRejected(string message) : Exception(message);
public sealed class DocumentProcessingUnavailable(bool retryable = false) : Exception("Document processing is not configured.") { public bool Retryable { get; } = retryable; }
