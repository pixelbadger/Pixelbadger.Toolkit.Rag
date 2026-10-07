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

    /// <summary>
    /// Logical path (metadata only) of the latest upload for a new document; afterwards the path of the latest
    /// SUCCESSFULLY indexed version (a pending re-ingest does not change it until it succeeds).
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Optional title (file name today). Used in the embedding document prompt.</summary>
    public string? Title { get; set; }

    /// <summary>Modality of the indexed version (see <see cref="SourcePath"/>).</summary>
    public Modality Modality { get; set; }

    /// <summary>Lower-case hex SHA-256 of the indexed file bytes; empty until the first ingest completes.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The canonical source file: the bytes of the latest successfully indexed version, written in the same
    /// transaction as its chunks. Null until the first successful ingest (and for documents that predate the column).
    /// Never loaded by queries that do not serve it.
    /// </summary>
    public byte[]? SourceContent { get; set; }

    /// <summary>Media type of <see cref="SourceContent"/>; set and cleared together with it.</summary>
    public string? ContentType { get; set; }

    /// <summary>Kept in step with the document's latest ingest job.</summary>
    public IndexStatus IndexStatus { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<Chunk> Chunks { get; set; } = new();

    /// <summary>The document's ingest jobs (history is kept; the latest one is shown).</summary>
    public List<IngestJob> Jobs { get; set; } = new();
}
