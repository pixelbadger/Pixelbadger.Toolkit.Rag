namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>Builds the vector index once ingest goes idle. Invoked by <see cref="Messaging.VectorIndexConsumer"/>.</summary>
public interface IVectorIndexService
{
    /// <summary>Called for every finished job: when no job is Queued or Processing, (re)builds the index and marks the keep-alive idle.</summary>
    Task OnJobFinishedAsync(CancellationToken cancellationToken);
}
