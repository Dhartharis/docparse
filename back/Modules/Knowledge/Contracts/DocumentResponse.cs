using DocParse.Modules.Knowledge.Domain;

namespace DocParse.Modules.Knowledge.Contracts;

// Browser contracts exclude storage keys and internal tenant relationships.
public sealed record DocumentSummary(Guid Id, string FileName, string? ContentType, string DocumentType, DateTime CreatedAt, bool HasOriginal, string ProcessingStatus, string? ProcessingError)
{
    public static DocumentSummary From(Document document) => new(document.Id, document.OriginalFileName ?? "document",
        document.OriginalContentType, document.Type, document.CreatedAt, !string.IsNullOrWhiteSpace(document.OriginalBlobName), document.ProcessingStatus, document.ProcessingError);
}

public sealed record DocumentDetails(DocumentSummary Document, IReadOnlyDictionary<string, string> Fields);
