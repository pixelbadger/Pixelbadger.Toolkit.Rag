using Microsoft.EntityFrameworkCore;
using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Persistence;

/// <summary>
/// EF Core model for the RAG store: dbo.Documents and dbo.Chunks_EG2_256 (one chunk table per
/// embedding model + dimension, so a model upgrade is a side-by-side table).
/// The vector index is deliberately NOT part of the model/migrations (100-row minimum, preview gating,
/// DACPAC limits); see <see cref="SqlDocumentStore.EnsureVectorIndexAsync"/>.
/// </summary>
public sealed class RagDbContext : DbContext
{
    public const string DocumentsTable = "Documents";
    public const string ChunksTable = "Chunks_EG2_256";
    public const int EmbeddingDimensions = 256;

    public RagDbContext(DbContextOptions<RagDbContext> options) : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(e =>
        {
            e.ToTable(DocumentsTable);
            e.HasKey(d => d.DocumentId);
            e.Property(d => d.GlobalId).HasColumnType("nvarchar(64)").IsRequired();
            e.HasIndex(d => d.GlobalId).IsUnique();
            e.Property(d => d.SourcePath).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(d => d.SourceId).HasColumnType("nvarchar(256)").IsRequired();
            e.HasIndex(d => d.SourceId);
            e.Property(d => d.Title).HasColumnType("nvarchar(1000)");
            e.Property(d => d.Modality).HasConversion<byte>().HasColumnType("tinyint");
            e.Property(d => d.ContentHash).HasColumnType("char(64)").IsRequired();
            e.Property(d => d.IndexStatus).HasConversion<byte>().HasColumnType("tinyint");
            e.Property(d => d.UpdatedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<Chunk>(e =>
        {
            e.ToTable(ChunksTable);
            // Int identity clustered PK: required by the SQL Server vector index.
            e.HasKey(c => c.ChunkId).IsClustered();
            e.Property(c => c.ChunkId).ValueGeneratedOnAdd();

            // Assigned in code (Guid v7: time-ordered, so the unique index appends rather than splits pages);
            // generating client-side means no round trip is needed to hand ids back to callers.
            e.Property(c => c.GlobalId).ValueGeneratedNever();
            e.HasIndex(c => c.GlobalId).IsUnique();

            e.HasOne(c => c.Document)
                .WithMany(d => d.Chunks)
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.DocumentId, c.Ordinal }).HasDatabaseName("IX_Chunks_Doc");

            e.Property(c => c.Modality).HasConversion<byte>().HasColumnType("tinyint");
            e.Property(c => c.ChunkText).HasColumnType("nvarchar(max)");
            e.Property(c => c.EmbeddingModel).HasColumnType("nvarchar(64)").IsRequired();
            e.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})").IsRequired();
        });
    }
}
