using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

public enum DeleteOutcome
{
    Deleted,
    NotFound,
    /// <summary>The document's running job did not stop within the cancel timeout; nothing was deleted.</summary>
    CancelTimedOut
}

/// <summary>
/// Orchestrates deleting a document: cancel its active ingest job, wait for the worker to let go, then remove the
/// SQL rows (chunks and jobs cascade).
/// </summary>
public sealed class DocumentService(
    IngestSettings settings,
    IDocumentStore store,
    IngestJobRegistry registry,
    ILogger<DocumentService> logger)
{
    public async Task<DeleteOutcome> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // A queued job needs no cancelling: it is deleted with the document. A running one is cancelled first, and we
        // wait for the worker so nothing is deleted underneath it.
        if (registry.CancelDocument(documentId) is { } finished)
        {
            logger.LogInformation("Cancelling the running ingest job of document {DocumentId} before deleting it", documentId);
            try
            {
                await finished.WaitAsync(settings.CancelTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("The ingest job of document {DocumentId} did not stop within {Timeout}", documentId, settings.CancelTimeout);
                return DeleteOutcome.CancelTimedOut;
            }
        }

        var deleted = await store.DeleteDocumentAsync(documentId, cancellationToken);
        if (!deleted)
            return DeleteOutcome.NotFound;

        // The worker may have claimed the document's queued job after the check above: stop it. Its write would be
        // refused anyway (the document is gone), this just saves the embedding work.
        _ = registry.CancelDocument(documentId);
        return DeleteOutcome.Deleted;
    }
}
