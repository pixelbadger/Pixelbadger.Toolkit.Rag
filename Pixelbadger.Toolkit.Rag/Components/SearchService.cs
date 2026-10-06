using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Hybrid search: Lucene BM25 + SQL vector, fused with RRF, hydrated from SQL.</summary>
public class SearchService : ISearchService
{
    public const int MaxQueryLength = 4096;
    public const int MaxResultsLimit = 100;
    public const int MaxDocumentIds = 100;

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
        IReadOnlyCollection<Guid>? documentIds = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(queryText, maxResults, documentIds);

        // Fetch more candidates from each side to improve fusion quality.
        var fetchCount = Math.Max(maxResults * 2, 20);

        var keywordTask = _lucene.SearchAsync(_options.IndexPath, queryText, fetchCount, documentIds, cancellationToken);
        var vectorTask = VectorSearchAsync(queryText, fetchCount, documentIds, cancellationToken);
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
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.EmbedQueryAsync(queryText, cancellationToken);
        return await _store.SearchAsync(embedding, fetchCount, documentIds, cancellationToken);
    }

    private static void ValidateSearchRequest(string queryText, int maxResults, IReadOnlyCollection<Guid>? documentIds)
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

        if (documentIds == null)
        {
            return;
        }

        if (documentIds.Count > MaxDocumentIds)
        {
            throw new ArgumentOutOfRangeException(nameof(documentIds), $"documentIds cannot contain more than {MaxDocumentIds} entries");
        }
    }
}
