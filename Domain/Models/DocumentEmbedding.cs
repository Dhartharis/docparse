using Pgvector;

namespace DocParse.Domain.Models;

public class DocumentEmbedding
{
    public Guid Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Vector? Embedding { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}