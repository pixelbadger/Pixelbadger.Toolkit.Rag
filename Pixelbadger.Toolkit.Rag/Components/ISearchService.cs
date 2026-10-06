using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Hybrid search: Lucene BM25 + SQL vector, fused with RRF, hydrated from SQL.</summary>
public interface ISearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string queryText, int maxResults = 10, IReadOnlyCollection<Guid>? documentIds = null, CancellationToken cancellationToken = default);
}
