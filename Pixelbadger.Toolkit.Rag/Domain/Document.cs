namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// A document: one uploaded file that is indexed, re-ingested and deleted as a unit. Table: dbo.Documents.
/// The row is created when the upload is accepted (status Queued, empty hash, no chunks yet) so its id can be
/// returned to the caller straight away.
/// </summary>
public sealed class Document
{
    /// <summary>Surrogate int identity primary key.</summary>
    public int DocumentId { get; set; }

    /// <summary>
    /// The document id callers see and filter by (unique). Assigned by the server when the document is created
    /// (<c>Guid.CreateVersion7()</c>); it has nothing to do with the path, so uploading the same path twice
    /// creates two documents.
    /// </summary>
    public Guid GlobalId { get; set; }

    /// <summary>Logical path of the latest upload (metadata only).</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Optional title (file name today). Used in the embedding document prompt.</summary>
    public string? Title { get; set; }

    /// <summary>Modality of the latest upload.</summary>
    public Modality Modality { get; set; }

    /// <summary>Lower-case hex SHA-256 of the indexed file bytes; empty until the first ingest completes.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Kept in step with the document's latest ingest job.</summary>
    public IndexStatus IndexStatus { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<Chunk> Chunks { get; set; } = new();

    /// <summary>The document's ingest jobs (history is kept; the latest one is shown).</summary>
    public List<IngestJob> Jobs { get; set; } = new();
}
