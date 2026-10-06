using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IContentIngester
{
    /// <summary>
    /// Ingests one text, image or audio file (routed by the logical path's extension, see <see cref="Domain.MediaTypes"/>).
    /// The chunks are written to the existing document <see cref="IngestSource.DocumentId"/> (<see cref="Persistence.DocumentNotFoundException"/> when it was deleted); the source path and title come from <see cref="IngestSource.LogicalPath"/>.
    /// Does not create the vector index: callers do that once per batch via <c>IDocumentStore.EnsureVectorIndexAsync</c>.
    /// </summary>
    Task<IngestResult> IngestAsync(IngestSource source, IngestOptions? options = null, CancellationToken cancellationToken = default);
}
