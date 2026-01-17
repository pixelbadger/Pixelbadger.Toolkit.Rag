namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// Search mode for queries.
/// </summary>
public enum SearchMode
{
    /// <summary>
    /// BM25 keyword search using Lucene.
    /// </summary>
    Bm25,

    /// <summary>
    /// Vector similarity search using embeddings.
    /// </summary>
    Vector,

    /// <summary>
    /// Hybrid search combining BM25 and vector search using Reciprocal Rank Fusion.
    /// </summary>
    Hybrid
}
