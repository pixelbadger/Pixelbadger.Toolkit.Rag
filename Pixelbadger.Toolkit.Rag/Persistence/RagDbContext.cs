using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Persistence;

/// <summary>
/// EF Core model for the RAG store: dbo.Documents and dbo.Chunks_EG2Q8_256 (one chunk table per
/// embedding model + dimension, so a model upgrade is a side-by-side table).
/// The vector index is deliberately NOT part of the model/migrations (100-row minimum, preview gating,
/// DACPAC limits); see <see cref="SqlDocumentStore.EnsureVectorIndexAsync"/>.
/// </summary>
public sealed class RagDbContext : DbContext
{
    public const string DocumentsTable = "Documents";
    public const string ChunksTable = "Chunks_EG2Q8_256";
    public const string IngestJobsTable = "IngestJobs";
    public const int EmbeddingDimensions = 256;

    public RagDbContext(DbContextOptions<RagDbContext> options) : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();

    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(e =>
        {
            e.ToTable(DocumentsTable);
            e.HasKey(d => d.DocumentId);
            // Assigned in code (Guid v7) when the document is created, so the id can be returned before any work is done.
            e.Property(d => d.GlobalId).ValueGeneratedNever();
            e.HasIndex(d => d.GlobalId).IsUnique();
            e.Property(d => d.SourcePath).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(d => d.Title).HasColumnType("nvarchar(1000)");
            e.Property(d => d.Modality).HasConversion<byte>().HasColumnType("tinyint");
            e.Property(d => d.ContentHash).HasColumnType("char(64)").IsRequired();
            // Nullable: only set once an ingest has succeeded. Select it explicitly (projection), never via the entity.
            e.Property(d => d.SourceContent).HasColumnType("varbinary(max)");
            e.Property(d => d.ContentType).HasColumnType("nvarchar(255)");
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

        modelBuilder.Entity<IngestJob>(e =>
        {
            e.ToTable(IngestJobsTable);
            e.HasKey(j => j.Id);
            // Assigned in code (Guid v7), like chunk ids.
            e.Property(j => j.Id).ValueGeneratedNever();
            e.HasOne(j => j.Document)
                .WithMany(d => d.Jobs)
                .HasForeignKey(j => j.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(j => j.Status).HasConversion<int>();
            e.Property(j => j.LogicalPath).HasColumnType("nvarchar(1024)").IsRequired();
            // Nullable: cleared once the job is terminal.
            e.Property(j => j.Content).HasColumnType("varbinary(max)");
            e.Property(j => j.Error).HasColumnType("nvarchar(max)");
            e.Property(j => j.CreatedAtUtc).HasColumnType("datetime2").HasConversion(UtcConverter);
            e.Property(j => j.StartedAtUtc).HasColumnType("datetime2").HasConversion(NullableUtcConverter);
            e.Property(j => j.CompletedAtUtc).HasColumnType("datetime2").HasConversion(NullableUtcConverter);
            e.HasIndex(j => new { j.Status, j.CreatedAtUtc }).HasDatabaseName("IX_IngestJobs_Status_CreatedAtUtc");
            e.HasIndex(j => new { j.DocumentId, j.CreatedAtUtc }).HasDatabaseName("IX_IngestJobs_Document_CreatedAtUtc");
        });
    }
}
