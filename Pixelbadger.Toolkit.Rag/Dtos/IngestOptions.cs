namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// Options for content ingestion.
/// </summary>
public class IngestOptions
{
    public const long DefaultMaxFileSizeBytes = 10 * 1024 * 1024;
    public const int DefaultMaxChunkCharacters = 20000;

    /// <summary>
    /// Maximum size for a single file read during ingestion.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>
    /// Maximum size for a single chunk before indexing or embedding.
    /// </summary>
    public int MaxChunkCharacters { get; set; } = DefaultMaxChunkCharacters;
}
