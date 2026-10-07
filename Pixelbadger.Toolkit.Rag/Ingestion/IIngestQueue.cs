using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>An uploaded file to enqueue. <see cref="OpenRead"/> is called once, while the job is being stored.</summary>
public sealed record IngestUpload(string LogicalPath, long SizeBytes, Func<Stream> OpenRead);

/// <summary>The document and job created for one upload of <see cref="IIngestQueue.EnqueueNewDocumentsAsync"/>.</summary>
public sealed record EnqueuedDocument(Guid DocumentId, Guid JobId);

public enum ReingestOutcome
{
    /// <summary>The document was idle: a new job was queued.</summary>
    Created,
    /// <summary>The document already had a queued job: its content was replaced in place.</summary>
    ReplacedQueued,
    /// <summary>The document's job is in progress; nothing was changed.</summary>
    Conflict,
    /// <summary>No such document.</summary>
    NotFound
}

/// <param name="JobId">The queued (or replaced) job; null for <see cref="ReingestOutcome.Conflict"/> and <see cref="ReingestOutcome.NotFound"/>.</param>
public sealed record ReingestResult(ReingestOutcome Outcome, Guid? JobId = null);

/// <summary>A job claimed by a worker.</summary>
public sealed record IngestJobClaim(
    Guid JobId,
    Guid DocumentId,
    int Attempts,
    int MaxChunkCharacters,
    string LogicalPath,
    long SizeBytes);

/// <summary>The terminal result recorded for a job. <see cref="Status"/> is Succeeded, Skipped or Failed.</summary>
public sealed record IngestJobOutcome(IngestJobStatus Status, int? ChunkCount = null, string? Error = null);

/// <summary>A job as returned by the document endpoints. Never carries file bytes.</summary>
public sealed record IngestJobDto(
    Guid JobId,
    IngestJobStatus Status,
    int Attempts,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? Error);

/// <summary>A document with its latest ingest job, as returned by <c>/api/documents</c>.</summary>
public sealed record DocumentDto(
    Guid DocumentId,
    string Path,
    string? Title,
    Modality Modality,
    IndexStatus IndexStatus,
    int ChunkCount,
    DateTime UpdatedAtUtc,
    IngestJobDto? LatestJob);

/// <summary>
/// Persistent ingest queue (SQL), organised around documents: each document has a series of jobs, one job is one
/// file for one document. Safe for concurrent claimers: a job is handed to exactly one claimer at a time, and a
/// claim that is not renewed lapses so the job can be retried.
/// </summary>
public interface IIngestQueue
{
    /// <summary>
    /// In ONE transaction creates, for each upload, a document (status Queued) and a queued job. Returns the ids in
    /// upload order. All or nothing.
    /// </summary>
    Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(
        IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-ingests an existing document. Race-safe: in one transaction the document's latest non-terminal job decides
    /// the outcome (Processing: <see cref="ReingestOutcome.Conflict"/>; Queued: its content is replaced in place;
    /// none: a new job is queued). A queued job that a worker claims in the meantime also yields Conflict, so an
    /// upload is never silently lost.
    /// </summary>
    Task<ReingestResult> EnqueueReingestAsync(
        Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the oldest job that is Queued, or Processing with an expired lease and attempts left
    /// (incrementing Attempts and setting the lease), and marks its document Processing. Jobs whose lease expired
    /// with no attempts left become Failed. Returns null when nothing is claimable.
    /// </summary>
    Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Streams a job's stored bytes into <paramref name="destination"/>.</summary>
    Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Extends the lease; false when the job is no longer Processing under <paramref name="owner"/>.</summary>
    Task<bool> ExtendLeaseAsync(Guid jobId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Records the job's terminal result, discards its stored bytes and brings the document's status in step.</summary>
    Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="error"/>; the job returns to Queued if attempts remain, else becomes Failed (and its
    /// bytes are dropped). The document's status follows.
    /// </summary>
    Task<IngestJobStatus> FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup recovery: puts every Processing job back to Queued (and its document to Queued) and returns how many.
    /// </summary>
    /// <remarks>
    /// Single-process assumption: this instance is the only worker, so anything still Processing belonged to a
    /// previous process that died. If the app is ever scaled out (multiple workers, so the in-process job registry
    /// no longer sees every job), remove this and rely on lease expiry in <see cref="TryClaimNextAsync"/> instead.
    /// </remarks>
    Task<int> ResetInFlightJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>A document with its latest job, or null when unknown.</summary>
    Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
}
