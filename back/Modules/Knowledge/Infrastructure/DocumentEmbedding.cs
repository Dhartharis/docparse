using Pgvector;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class DocumentEmbedding
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string EmbeddingProfile { get; set; } = "openai:text-embedding-3-small";
    public int? PageNumber { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = string.Empty;
    public Vector? Embedding { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
