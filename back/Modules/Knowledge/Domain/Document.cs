namespace DocParse.Modules.Knowledge.Domain;

public class Document
{
    public DateTime? DeletedAt { get; set; }
    public byte[]? ContentHash { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OrganizationId { get; set; }
    public Guid? KnowledgeBaseId { get; set; }

    public string ProcessingStatus { get; set; } = "ready";
    public int ProcessingAttempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime ProcessingUpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? ProcessingError { get; set; }
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
    public string? OriginalBlobName { get; set; }
    public string? OriginalFileName { get; set; }
    public string? OriginalContentType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
