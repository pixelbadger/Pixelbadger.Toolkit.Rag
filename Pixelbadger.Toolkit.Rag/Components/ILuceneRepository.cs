namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>A text chunk to put in the BM25 index, keyed by SQL ChunkId.</summary>
public sealed record LuceneChunk(int ChunkId, string Text);

/// <summary>A BM25 hit. ChunkId refers to the SQL chunk row.</summary>
public sealed record KeywordHit(int ChunkId, float Score);

/// <summary>
/// Lucene.NET BM25 index. Holds only what BM25 needs (indexed text, chunk_id, document_id);
/// all content and metadata live in SQL and are hydrated from there.
/// </summary>
public interface ILuceneRepository
{
    /// <summary>Deletes all entries for <paramref name="documentId"/>, then adds <paramref name="chunks"/>. One commit.</summary>
    Task ReplaceDocumentAsync(string indexPath, Guid documentId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Deletes all entries for <paramref name="documentId"/>. A no-op when the index does not exist yet.</summary>
    Task DeleteDocumentAsync(string indexPath, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>BM25 search, descending score. Returns empty if the index does not exist yet.</summary>
    Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default);
}
