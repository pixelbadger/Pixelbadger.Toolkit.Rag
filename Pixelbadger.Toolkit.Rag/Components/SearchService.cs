using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Hybrid search: Lucene BM25 + SQL vector, fused with RRF, hydrated from SQL.</summary>
public class SearchService : ISearchService
{
    public const int MaxQueryLength = 4096;
    public const int MaxResultsLimit = 100;
    public const int MaxSourceIds = 100;
    public const int MaxSourceIdLength = 256;

    private readonly RagOptions _options;
    private readonly ILuceneRepository _lucene;
    private readonly IDocumentStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly IReranker _reranker;

    public SearchService(
        RagOptions options,
        ILuceneRepository lucene,
        IDocumentStore store,
        IEmbeddingService embeddings,
        IReranker reranker)
    {
        _options = options;
        _lucene = lucene;
        _store = store;
        _embeddings = embeddings;
        _reranker = reranker;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string queryText,
        int maxResults = 10,
        IReadOnlyCollection<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(queryText, maxResults, sourceIds);

        // Fetch more candidates from each side to improve fusion quality.
        var fetchCount = Math.Max(maxResults * 2, 20);

        var keywordTask = _lucene.SearchAsync(_options.IndexPath, queryText, fetchCount, sourceIds, cancellationToken);
        var vectorTask = VectorSearchAsync(queryText, fetchCount, sourceIds, cancellationToken);
        await Task.WhenAll(keywordTask, vectorTask);

        var keywordIds = keywordTask.Result.Select(h => h.ChunkId).ToList();
        var vectorIds = vectorTask.Result.Select(h => h.ChunkId).ToList();

        var fused = _reranker.Fuse(keywordIds, vectorIds, maxResults);
        if (fused.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var records = await _store.GetChunksAsync(fused.Select(f => f.ChunkId).ToList(), cancellationToken);
        var byId = records.ToDictionary(r => r.ChunkId);

        var results = new List<SearchResult>(fused.Count);
        foreach (var hit in fused)
        {
            // A chunk can be missing from SQL if the Lucene index is stale; skip it.
            if (!byId.TryGetValue(hit.ChunkId, out var record))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Score = hit.Score,
                ChunkId = record.ChunkGlobalId,
                DocumentId = record.DocumentGlobalId,
                SourcePath = record.SourcePath,
                SourceFile = Path.GetFileName(record.SourcePath),
                SourceId = record.SourceId,
                Ordinal = record.Ordinal,
                Modality = record.Modality,
                LocatorStart = record.LocatorStart,
                LocatorEnd = record.LocatorEnd,
                Content = record.Text,
                KeywordRank = hit.KeywordRank,
                VectorRank = hit.VectorRank
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<VectorHit>> VectorSearchAsync(
        string queryText,
        int fetchCount,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.EmbedQueryAsync(queryText, cancellationToken);
        return await _store.SearchAsync(embedding, fetchCount, sourceIds, cancellationToken);
    }

    private static void ValidateSearchRequest(string queryText, int maxResults, IReadOnlyCollection<string>? sourceIds)
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

        if (sourceIds.Count > MaxSourceIds)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIds), $"sourceIds cannot contain more than {MaxSourceIds} entries");
        }

        if (sourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("sourceIds cannot contain empty values", nameof(sourceIds));
        }

        if (sourceIds.Any(sourceId => sourceId.Length > MaxSourceIdLength))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIds), $"sourceIds entries cannot exceed {MaxSourceIdLength} characters");
        }
    }
}
