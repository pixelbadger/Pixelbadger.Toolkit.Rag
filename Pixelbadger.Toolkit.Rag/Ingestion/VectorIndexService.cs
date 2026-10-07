using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Builds the vector index once ingest goes idle (see <see cref="IVectorIndexService"/>). Every finished job calls this, but
/// only the last one to finish (no job still Queued or Processing) builds the index, so a batch of uploads triggers a single
/// build. The index check is idempotent and creates the index only when enough rows exist; the keep-alive is marked idle
/// only after it succeeds, so a failed check is redelivered by the bus rather than hiding behind an idle host.
/// </summary>
public sealed class VectorIndexService(
    IIngestQueue queue,
    IDocumentStore documents,
    IIngestKeepAlive keepAlive,
    ILogger<VectorIndexService> logger) : IVectorIndexService
{
    /// <inheritdoc />
    public async Task OnJobFinishedAsync(CancellationToken cancellationToken)
    {
        // More work is coming; the job that finishes last will build the index.
        if (await queue.HasActiveJobsAsync(cancellationToken))
            return;

        logger.LogInformation("Ingest queue is idle; checking the vector index");
        await documents.EnsureVectorIndexAsync(cancellationToken);
        await keepAlive.MarkIdleAsync(cancellationToken);
    }
}
