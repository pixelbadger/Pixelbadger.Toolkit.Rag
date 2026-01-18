using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface ILuceneRepository
{
    Task IndexWithLuceneAsync(string indexPath, string contentPath, List<IChunk> chunks);
    Task<List<SearchResult>> QueryLuceneAsync(string indexPath, string queryText, int maxResults, string[]? sourceIds);
}
