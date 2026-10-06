namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>A text chunk to put in the BM25 index, keyed by SQL ChunkId.</summary>
public sealed record LuceneChunk(int ChunkId, string Text);

/// <summary>A BM25 hit. ChunkId refers to the SQL chunk row.</summary>
public sealed record KeywordHit(int ChunkId, float Score);

/// <summary>
/// Lucene.NET BM25 index. Holds only what BM25 needs (indexed text, chunk_id, document_id, source_id);
/// all content and metadata live in SQL and are hydrated from there.
/// </summary>
public interface ILuceneRepository
{
    /// <summary>Deletes all entries for <paramref name="documentGlobalId"/>, then adds <paramref name="chunks"/>. One commit.</summary>
    Task ReplaceDocumentAsync(string indexPath, string documentGlobalId, string sourceId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>BM25 search, descending score. Returns empty if the index does not exist yet.</summary>
    Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<string>? sourceIds, CancellationToken cancellationToken = default);
}
