namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>Lifecycle of an ingest job. Persisted as int.</summary>
public enum IngestJobStatus
{
    Queued = 0,
    Processing = 1,
    /// <summary>The file was indexed.</summary>
    Succeeded = 2,
    /// <summary>The file was read but produced no chunks (empty content).</summary>
    Skipped = 3,
    /// <summary>The file could not be ingested (content error, or the job failed on every attempt).</summary>
    Failed = 4
}

/// <summary>
/// One queued upload of one file for one document. Table: dbo.IngestJobs. A document has a series of jobs
/// (create, then each re-ingest); a document delete cascades to its jobs.
/// </summary>
public sealed class IngestJob
{
    public Guid Id { get; set; }

    /// <summary>FK to <see cref="Document.DocumentId"/> (cascade delete).</summary>
    public int DocumentId { get; set; }

    public Document? Document { get; set; }

    public IngestJobStatus Status { get; set; }

    /// <summary>Incremented each time processing of the job begins (again after a crash).</summary>
    public int Attempts { get; set; }

    public int MaxChunkCharacters { get; set; }

    /// <summary>Caller-supplied, validated relative path (always '/'-separated).</summary>
    public string LogicalPath { get; set; } = string.Empty;

    /// <summary>The uploaded bytes. Set to null once the job is terminal so blobs do not accumulate.</summary>
    public byte[]? Content { get; set; }

    public long SizeBytes { get; set; }

    public int? ChunkCount { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public static bool IsTerminal(IngestJobStatus status) => status >= IngestJobStatus.Succeeded;
}
