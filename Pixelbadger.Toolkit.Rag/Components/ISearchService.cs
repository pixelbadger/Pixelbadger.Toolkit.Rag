using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Vector search over SQL Server (cosine similarity), hydrated from SQL.</summary>
public interface ISearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string queryText, int maxResults = 10, IReadOnlyCollection<Guid>? documentIds = null, CancellationToken cancellationToken = default);
}
