using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IContentIngester
{
    /// <summary>Ingests one text, image or audio file (routed by <see cref="Domain.MediaTypes"/>).</summary>
    Task<IngestResult> IngestFileAsync(string filePath, IngestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Recursively ingests all supported files under a folder; per-file failures are reported, not thrown.</summary>
    Task<IngestSummary> IngestFolderAsync(string folderPath, IngestOptions? options = null, CancellationToken cancellationToken = default);
}
