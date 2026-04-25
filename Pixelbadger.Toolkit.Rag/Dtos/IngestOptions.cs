namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// Options for content ingestion.
/// </summary>
public class IngestOptions
{
    public const long DefaultMaxFileSizeBytes = 10 * 1024 * 1024;
    public const int DefaultMaxFiles = 1000;
    public const int DefaultMaxChunkCharacters = 20000;

    /// <summary>
    /// Enable vector storage using sqlite-vec alongside Lucene BM25 indexing.
    /// </summary>
    public bool EnableVectorStorage { get; set; } = true;

    /// <summary>
    /// Maximum size for a single file read during ingestion.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>
    /// Maximum number of supported files to ingest from a folder.
    /// </summary>
    public int MaxFiles { get; set; } = DefaultMaxFiles;

    /// <summary>
    /// Maximum size for a single chunk before indexing or embedding.
    /// </summary>
    public int MaxChunkCharacters { get; set; } = DefaultMaxChunkCharacters;

    /// <summary>
    /// Whether folder ingestion may follow symbolic links and other reparse points.
    /// </summary>
    public bool AllowSymlinks { get; set; } = false;
}
