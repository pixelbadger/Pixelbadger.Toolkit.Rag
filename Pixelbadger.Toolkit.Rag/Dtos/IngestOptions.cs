namespace Pixelbadger.Toolkit.Rag.Dtos;

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
