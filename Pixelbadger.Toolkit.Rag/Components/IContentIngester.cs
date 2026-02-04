using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IContentIngester
{
    Task IngestContentAsync(string indexPath, string contentPath);
    Task IngestContentAsync(string indexPath, string contentPath, IngestOptions? options);
    Task IngestFolderAsync(string indexPath, string folderPath, IngestOptions? options = null);
}
