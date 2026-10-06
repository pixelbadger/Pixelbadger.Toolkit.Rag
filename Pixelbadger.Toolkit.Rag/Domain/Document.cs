namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// A source document (one ingested file). Table: dbo.Documents.
/// </summary>
public sealed class Document
{
    /// <summary>Surrogate int identity primary key.</summary>
    public int DocumentId { get; set; }

    /// <summary>
    /// Stable, system-global document id (unique). Derived deterministically from the
    /// normalised absolute source path via <see cref="DocumentIds.FromSourcePath"/>, so
    /// re-ingesting the same file replaces the same document.
    /// </summary>
    public string GlobalId { get; set; } = string.Empty;

    /// <summary>Absolute path of the ingested file.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>File name without extension; used by the sourceIds search filter.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Optional title (file name today). Used in the embedding document prompt.</summary>
    public string? Title { get; set; }

    /// <summary>Modality of the source file.</summary>
    public Modality Modality { get; set; }

    /// <summary>Lower-case hex SHA-256 of the file bytes.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public IndexStatus IndexStatus { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<Chunk> Chunks { get; set; } = new();
}
