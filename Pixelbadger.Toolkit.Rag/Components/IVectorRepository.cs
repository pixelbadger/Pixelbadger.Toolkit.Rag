using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IVectorRepository
{
    Task StoreVectorsAsync(string indexPath, string contentPath, List<IChunk> chunks);
    Task<List<SearchResult>> QueryVectorsAsync(string indexPath, string queryText, int maxResults, string[]? sourceIds);
    bool Exists(string indexPath);
}
