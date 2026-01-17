namespace Pixelbadger.Toolkit.Rag.Components;

public class SearchResult
{
    public float Score { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int ParagraphNumber { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
}

/// <summary>
/// Options for content ingestion.
/// </summary>
public class IngestOptions
{
    /// <summary>
    /// Enable vector storage using sqlite-vec alongside Lucene BM25 indexing.
    /// </summary>
    public bool EnableVectorStorage { get; set; }
}

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