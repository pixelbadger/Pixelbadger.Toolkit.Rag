using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface ISearchService
{
    Task<List<SearchResult>> SearchAsync(string indexPath, string queryText, SearchMode mode, int maxResults = 10, string[]? sourceIds = null);
    Task<List<SearchResult>> VectorQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null);
    Task<List<SearchResult>> HybridQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null);
}
