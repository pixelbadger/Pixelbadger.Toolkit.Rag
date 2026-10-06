using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Persistence;

/// <summary>Document metadata for <see cref="IDocumentStore.ReplaceDocumentAsync"/>.</summary>
public sealed record DocumentDraft(
    string GlobalId,
    string SourcePath,
    string SourceId,
    string? Title,
    Modality Modality,
    string ContentHash);

/// <summary>A chunk to persist. <see cref="Embedding"/> must already be truncated + re-normalised.</summary>
public sealed record ChunkDraft(
    int Ordinal,
    Modality Modality,
    long? LocatorStart,
    long? LocatorEnd,
    string? Text,
    float[] Embedding);

/// <summary>A persisted chunk, as returned after write and for result hydration.</summary>
public sealed record ChunkRecord(
    int ChunkId,
    Guid ChunkGlobalId,
    int DocumentId,
    string DocumentGlobalId,
    string SourcePath,
    string SourceId,
    string? Title,
    int Ordinal,
    Modality Modality,
    long? LocatorStart,
    long? LocatorEnd,
    string? Text);

/// <summary>A vector search hit. Distance is cosine distance (1 - similarity).</summary>
public sealed record VectorHit(int ChunkId, float Distance);

/// <summary>
/// SQL Server persistence for documents, chunks and vectors.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Applies EF Core migrations (creating the database if needed). Idempotent.</summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// In one transaction: upsert the document by GlobalId, delete its existing chunks,
    /// insert <paramref name="chunks"/>, set IndexStatus = Indexed. Returns the persisted
    /// chunks (with ChunkId / ChunkGlobalId populated) in ordinal order.
    /// </summary>
    Task<IReadOnlyList<ChunkRecord>> ReplaceDocumentAsync(
        DocumentDraft document,
        IReadOnlyList<ChunkDraft> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a document's IndexStatus (e.g. Failed when the Lucene write fails).</summary>
    Task SetIndexStatusAsync(string documentGlobalId, IndexStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nearest chunks to <paramref name="queryEmbedding"/> (256-d, unit length), ascending distance.
    /// Optional filter on Document.SourceId. Uses approximate VECTOR_SEARCH when the vector
    /// index exists and <see cref="SqlStoreOptions.SearchMode"/> allows it, else exact VECTOR_DISTANCE.
    /// </summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryEmbedding,
        int maxResults,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken = default);

    /// <summary>Hydrates chunks by ChunkId. Missing ids are omitted; order unspecified.</summary>
    Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyCollection<int> chunkIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent: if no vector index exists and the chunk table holds at least 100 rows, create it
    /// (DiskANN, cosine). No-op in exact-only mode. Never throws for "not enough rows".
    /// </summary>
    Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default);
}
