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
/// Persistent ingest jobs (SQL), organised around documents: each document has a series of jobs, one job is one
/// file for one document. Every job status change publishes a <see cref="Messaging.JobStatusChanged"/> event in the
/// same transaction as the write (SlimMessageBus outbox, see <see cref="Messaging.JobEventTransaction"/>): the bus,
/// not polling, drives the work.
/// </summary>
public interface IIngestQueue
{
    /// <summary>
    /// In ONE transaction creates, for each upload, a document (status Queued) and a queued job, and publishes
    /// <c>JobStatusChanged(job, Queued)</c> for each. Returns the ids in upload order. All or nothing.
    /// </summary>
    Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(
        IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-ingests an existing document. Race-safe: in one transaction the document's latest non-terminal job decides
    /// the outcome (Processing: <see cref="ReingestOutcome.Conflict"/>; Queued: its content is replaced in place, no
    /// event since the status is unchanged; none: a new job is queued and <c>JobStatusChanged(job, Queued)</c> is
    /// published). A queued job that is picked up in the meantime also yields Conflict, so an upload is never
    /// silently lost.
    /// </summary>
    Task<ReingestResult> EnqueueReingestAsync(
        Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts (or, after a crash, restarts) a job: when the job is Queued or Processing it becomes Processing,
    /// Attempts is incremented, StartedAtUtc is set if unset, its document becomes Processing and
    /// <c>JobStatusChanged(job, Processing)</c> is published, all in one transaction. Returns null (and changes
    /// nothing) when the job no longer exists or is already terminal.
    /// </summary>
    Task<IngestJobClaim?> BeginProcessingAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Streams a job's stored bytes into <paramref name="destination"/>.</summary>
    Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the job's terminal result, discards its stored bytes, brings the document's status in step and
    /// publishes <c>JobStatusChanged(job, outcome.Status)</c>, in one transaction. A job that no longer exists (deleted
    /// with its document) is a quiet no-op with no event.
    /// </summary>
    Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>True while any job is Queued or Processing.</summary>
    Task<bool> HasActiveJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>A document with its latest job, or null when unknown.</summary>
    Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of jobs (any status, or only <paramref name="status"/>), newest first (CreatedAtUtc, then Id, descending).
    /// <paramref name="page"/> is 1-based. Stored bytes and lease details are never read.
    /// </summary>
    Task<IngestJobPage> GetJobsAsync(
        int page, int pageSize, IngestJobStatus? status = null, CancellationToken cancellationToken = default);
}

/// <summary>A job as listed by <c>/api/jobs</c>. Never carries file bytes or lease details.</summary>
/// <param name="ChunkCount">The job's own terminal chunk count (not the document's current total); null until it completes.</param>
public sealed record IngestJobListItemDto(
    Guid JobId,
    Guid DocumentId,
    string Path,
    IngestJobStatus Status,
    int Attempts,
    int MaxChunkCharacters,
    long SizeBytes,
    int? ChunkCount,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? Error);

/// <summary>One page of jobs, newest first. <see cref="TotalCount"/> counts all jobs matching the filter.</summary>
public sealed record IngestJobPage(IReadOnlyList<IngestJobListItemDto> Jobs, int Page, int PageSize, int TotalCount);
