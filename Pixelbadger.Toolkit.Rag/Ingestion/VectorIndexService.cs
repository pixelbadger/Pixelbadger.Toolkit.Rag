namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>Builds the vector index when ingest goes idle (see <see cref="IVectorIndexService"/>).</summary>
public sealed class VectorIndexService : IVectorIndexService
{
    public Task OnJobFinishedAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("Stream 2b");
}
