using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public class SearchService : ISearchService
{
    public const int MaxQueryLength = 4096;
    public const int MaxResultsLimit = 100;
    public const int MaxSourceIds = 100;
    public const int MaxSourceIdLength = 256;

    private readonly ILuceneRepository _luceneRepo;
    private readonly IVectorRepository _vectorRepo;
    private readonly IReranker _reranker;

    public SearchService(
        ILuceneRepository luceneRepo,
        IVectorRepository vectorRepo,
        IReranker reranker)
    {
        _luceneRepo = luceneRepo;
        _vectorRepo = vectorRepo;
        _reranker = reranker;
    }

    public async Task<List<SearchResult>> SearchAsync(string indexPath, string queryText, SearchMode mode, int maxResults = 10, string[]? sourceIds = null)
    {
        ValidateSearchRequest(queryText, maxResults, sourceIds);

        return mode switch
        {
            SearchMode.Bm25 => await _luceneRepo.QueryLuceneAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Vector => await VectorQueryAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Hybrid => await HybridQueryAsync(indexPath, queryText, maxResults, sourceIds),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown search mode")
        };
    }

    private static void ValidateSearchRequest(string queryText, int maxResults, string[]? sourceIds)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new ArgumentException("Query is required", nameof(queryText));
        }

        if (queryText.Length > MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(queryText), $"Query length cannot exceed {MaxQueryLength} characters");
        }

        if (maxResults < 1 || maxResults > MaxResultsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), $"maxResults must be between 1 and {MaxResultsLimit}");
        }

        if (sourceIds == null)
        {
            return;
        }

        if (sourceIds.Length > MaxSourceIds)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIds), $"sourceIds cannot contain more than {MaxSourceIds} entries");
        }

        if (sourceIds.Any(sourceId => string.IsNullOrWhiteSpace(sourceId)))
        {
            throw new ArgumentException("sourceIds cannot contain empty values", nameof(sourceIds));
        }

        if (sourceIds.Any(sourceId => sourceId.Length > MaxSourceIdLength))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIds), $"sourceIds entries cannot exceed {MaxSourceIdLength} characters");
        }
    }

    private async Task<List<SearchResult>> VectorQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        return await _vectorRepo.QueryVectorsAsync(indexPath, queryText, maxResults, sourceIds);
    }

    private async Task<List<SearchResult>> HybridQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        // Fetch more results from each search to improve fusion quality
        var fetchCount = Math.Max(maxResults * 2, 20);

        // Run both searches in parallel
        var bm25Task = _luceneRepo.QueryLuceneAsync(indexPath, queryText, fetchCount, sourceIds);
        var vectorTask = VectorQueryAsync(indexPath, queryText, fetchCount, sourceIds);

        await Task.WhenAll(bm25Task, vectorTask);

        var bm25Results = bm25Task.Result;
        var vectorResults = vectorTask.Result;

        // Apply Reranker
        return _reranker.RerankResults(bm25Results, vectorResults, maxResults);
    }
}
