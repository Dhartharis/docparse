using DocParse.Modules.Knowledge.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector.EntityFrameworkCore;
using System.Text.Json;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class KnowledgeDbContext : DbContext
{
    public KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : base(options) { }

    public DbSet<DocumentEmbedding> DocumentEmbeddings { get; set; }
    public DbSet<Document> Documents { get; set; }
    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.Entity<KnowledgeBase>(entity =>
        {
            entity.ToTable("knowledge_bases", "knowledge");
            entity.Property(x=>x.DeletedAt).HasColumnName("deleted_at");
            entity.HasQueryFilter(x=>x.DeletedAt==null);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        });

        var fieldsConverter = new ValueConverter<Dictionary<string, string>, string>(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());
        var fieldsComparer = new ValueComparer<Dictionary<string, string>>(
            (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new());

        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("documents", "knowledge");
            entity.Property(x=>x.DeletedAt).HasColumnName("deleted_at");
            entity.Property(x=>x.ContentHash).HasColumnName("content_hash");
            entity.Property(x=>x.OriginalSizeBytes).HasColumnName("original_size_bytes");
            entity.HasQueryFilter(x=>x.DeletedAt==null);
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Id).HasColumnName("id");
            entity.Property(document => document.OrganizationId).HasColumnName("organization_id");
            entity.Property(document => document.KnowledgeBaseId).HasColumnName("knowledge_base_id");
            entity.Property(x => x.ProcessingStatus).HasColumnName("processing_status");
            entity.Property(x => x.ProcessingAttempts).HasColumnName("processing_attempts");
            entity.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(x => x.ProcessingUpdatedAt).HasColumnName("processing_updated_at");
            entity.Property(x => x.LeaseId).HasColumnName("lease_id");
            entity.Property(x => x.LeaseUntil).HasColumnName("lease_until");
            entity.Property(x => x.ProcessingError).HasColumnName("processing_error");
            entity.Property(document => document.Type).HasColumnName("type");
            entity.Property(document => document.Fields)
                .HasColumnName("fields")
                .HasColumnType("jsonb")
                .HasConversion(fieldsConverter)
                .Metadata.SetValueComparer(fieldsComparer);
            entity.Property(document => document.OriginalBlobName).HasColumnName("original_blob_name");
            entity.Property(document => document.OriginalFileName).HasColumnName("original_file_name");
            entity.Property(document => document.OriginalContentType).HasColumnName("original_content_type");
            entity.Property(document => document.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<DocumentEmbedding>(entity =>
        {
            entity.ToTable("document_embeddings", "knowledge");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.EmbeddingProfile).HasColumnName("embedding_profile").HasMaxLength(300);
            entity.Property(e => e.PageNumber).HasColumnName("page_number");
            entity.Property(e => e.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.Embedding).HasColumnName("embedding").HasColumnType("vector");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.HasOne<Document>()
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
