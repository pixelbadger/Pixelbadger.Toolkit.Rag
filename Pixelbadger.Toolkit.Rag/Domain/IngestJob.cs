namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>Lifecycle of an ingest job. Persisted as int.</summary>
public enum IngestJobStatus
{
    Queued = 0,
    Processing = 1,
    /// <summary>All files reached a terminal state (per-file failures do not fail the job).</summary>
    Completed = 2,
    /// <summary>The job itself failed on every attempt.</summary>
    Failed = 3
}

/// <summary>Lifecycle of one uploaded file in a job. Persisted as int.</summary>
public enum IngestFileStatus
{
    Queued = 0,
    Succeeded = 1,
    Failed = 2,
    /// <summary>Ingested but produced no chunks (empty content).</summary>
    Skipped = 3
}

/// <summary>A queued upload batch. Table: dbo.IngestJobs.</summary>
public sealed class IngestJob
{
    public Guid Id { get; set; }

    public IngestJobStatus Status { get; set; }

    /// <summary>Incremented each time a worker claims the job.</summary>
    public int Attempts { get; set; }

    public int MaxChunkCharacters { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>While Processing: when the claim lapses and another worker may take the job over.</summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Instance id of the worker holding the lease.</summary>
    public string? LeaseOwner { get; set; }

    public string? Error { get; set; }

    public List<IngestJobFile> Files { get; set; } = new();
}

/// <summary>One uploaded file of a job. Table: dbo.IngestJobFiles.</summary>
public sealed class IngestJobFile
{
    public int Id { get; set; }

    public Guid JobId { get; set; }

    public IngestJob? Job { get; set; }

    /// <summary>Position within the upload (processing order).</summary>
    public int Ordinal { get; set; }

    /// <summary>Caller-supplied, validated relative path (always '/'-separated); the document identity.</summary>
    public string LogicalPath { get; set; } = string.Empty;

    /// <summary>The uploaded bytes. Set to null once the file is terminal so blobs do not accumulate.</summary>
    public byte[]? Content { get; set; }

    public long SizeBytes { get; set; }

    public IngestFileStatus Status { get; set; }

    public string? DocumentGlobalId { get; set; }

    public Modality? Modality { get; set; }

    public int? ChunkCount { get; set; }

    public string? Error { get; set; }
}
