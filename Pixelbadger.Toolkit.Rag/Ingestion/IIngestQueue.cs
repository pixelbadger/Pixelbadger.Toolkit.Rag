using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>An uploaded file to enqueue. <see cref="OpenRead"/> is called once, while the job is being stored.</summary>
public sealed record IngestUpload(string LogicalPath, long SizeBytes, Func<Stream> OpenRead);

/// <summary>A job claimed by a worker.</summary>
public sealed record IngestJobClaim(Guid JobId, int Attempts, int MaxChunkCharacters);

/// <summary>A file of a claimed job that has not reached a terminal state yet (content is read separately).</summary>
public sealed record PendingIngestFile(int FileId, int Ordinal, string LogicalPath, long SizeBytes);

/// <summary>The terminal result recorded for one file.</summary>
public sealed record IngestFileOutcome(
    IngestFileStatus Status,
    string? DocumentGlobalId = null,
    Modality? Modality = null,
    int? ChunkCount = null,
    string? Error = null);

public sealed record IngestFileStatusDto(
    string Path,
    IngestFileStatus Status,
    string? DocumentId,
    Modality? Modality,
    int? ChunkCount,
    string? Error);

public sealed record IngestJobSummaryDto(int Succeeded, int Failed, int Skipped);

/// <summary>Job status as returned by <c>GET /api/ingest/{jobId}</c>. Never carries file bytes.</summary>
public sealed record IngestJobStatusDto(
    Guid JobId,
    IngestJobStatus Status,
    int Attempts,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? Error,
    IReadOnlyList<IngestFileStatusDto> Files,
    IngestJobSummaryDto Summary);

/// <summary>
/// Persistent ingest job queue (SQL). Safe for concurrent claimers: a job is handed to exactly one
/// claimer at a time, and a claim that is not renewed lapses so the job can be retried.
/// </summary>
public interface IIngestQueue
{
    /// <summary>Stores a job and its files in one transaction and returns the job id.</summary>
    Task<Guid> EnqueueAsync(IReadOnlyList<IngestUpload> files, int maxChunkCharacters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the oldest job that is Queued, or Processing with an expired lease and attempts left
    /// (incrementing Attempts and setting the lease). Jobs whose lease expired with no attempts left become Failed.
    /// Returns null when nothing is claimable.
    /// </summary>
    Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Files of the job that are not terminal yet, in upload order (metadata only).</summary>
    Task<IReadOnlyList<PendingIngestFile>> GetPendingFilesAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Streams a pending file's stored bytes into <paramref name="destination"/>.</summary>
    Task ReadFileContentAsync(int fileId, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Records a file's terminal result and discards its stored bytes.</summary>
    Task CompleteFileAsync(int fileId, IngestFileOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Extends the lease; false when the job is no longer Processing under <paramref name="owner"/>.</summary>
    Task<bool> ExtendLeaseAsync(Guid jobId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    Task CompleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="error"/>; the job returns to Queued if attempts remain, else becomes Failed.</summary>
    Task<IngestJobStatus> FailJobAsync(Guid jobId, string error, CancellationToken cancellationToken = default);

    /// <summary>Job status without file bytes, or null when unknown.</summary>
    Task<IngestJobStatusDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
