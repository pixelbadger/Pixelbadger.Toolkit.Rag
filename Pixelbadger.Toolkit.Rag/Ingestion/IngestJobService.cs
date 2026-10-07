namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>Runs one ingest job end to end (see <see cref="IIngestJobService"/>).</summary>
public sealed class IngestJobService : IIngestJobService
{
    public Task ProcessAsync(Guid jobId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Stream 2a");
}
