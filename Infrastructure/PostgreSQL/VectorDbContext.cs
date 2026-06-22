using DocParse.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DocParse.Infrastructure.PostgreSQL;

public class VectorDbContext : DbContext
{
    public VectorDbContext(DbContextOptions<VectorDbContext> options) : base(options) { }

    public DbSet<DocumentEmbedding> DocumentEmbeddings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<DocumentEmbedding>(entity =>
        {
            entity.ToTable("document_embeddings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.Embedding).HasColumnName("embedding");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });
    }
}