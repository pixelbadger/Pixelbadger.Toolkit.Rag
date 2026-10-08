using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>
/// Raised (through the outbox, in the same transaction as the write) every time an ingest job's status changes:
/// created or re-queued (<see cref="IngestJobStatus.Queued"/>), picked up (<see cref="IngestJobStatus.Processing"/>)
/// and finished (Succeeded, Skipped, Failed). Carries no file bytes: consumers read the job from SQL.
/// </summary>
public sealed record JobStatusChanged(Guid JobId, IngestJobStatus Status);
