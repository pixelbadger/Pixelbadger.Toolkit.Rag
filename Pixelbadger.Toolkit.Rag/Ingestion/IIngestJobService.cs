namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>Runs one ingest job (one file for one document). Invoked by <see cref="Messaging.IngestJobConsumer"/>.</summary>
public interface IIngestJobService
{
    /// <summary>
    /// Processes the job if it is still Queued (or Processing after a crash: the bus redelivers the event once the
    /// message lock expires). A missing or finished job is a quiet no-op. Problems with the file complete the job
    /// Failed; infrastructure errors propagate so the bus redelivers, until the job's attempts are used up.
    /// </summary>
    Task ProcessAsync(Guid jobId, CancellationToken cancellationToken);
}
