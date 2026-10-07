using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Vector search over SQL Server (cosine similarity), hydrated from SQL.</summary>
public class SearchService : ISearchService
{
    public const int MaxQueryLength = 4096;
    public const int MaxResultsLimit = 100;
    public const int MaxDocumentIds = 100;

    private readonly IDocumentStore _store;
    private readonly IEmbeddingService _embeddings;

    public SearchService(IDocumentStore store, IEmbeddingService embeddings)
    {
        _store = store;
        _embeddings = embeddings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string queryText,
        int maxResults = 10,
        IReadOnlyCollection<Guid>? documentIds = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(queryText, maxResults, documentIds);

        var embedding = await _embeddings.EmbedQueryAsync(queryText, cancellationToken);
        var hits = await _store.SearchAsync(embedding, maxResults, documentIds, cancellationToken);
        if (hits.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var records = await _store.GetChunksAsync(hits.Select(h => h.ChunkId).ToList(), cancellationToken);
        var byId = records.ToDictionary(r => r.ChunkId);

        var results = new List<SearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            // A chunk can be missing from SQL if its document was deleted between the search and the hydrate; skip it.
            if (!byId.TryGetValue(hit.ChunkId, out var record))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                // The store reports cosine distance; callers get cosine similarity.
                Score = 1f - hit.Distance,
                ChunkId = record.ChunkGlobalId,
                DocumentId = record.DocumentGlobalId,
                SourcePath = record.SourcePath,
                SourceFile = Path.GetFileName(record.SourcePath),
                Ordinal = record.Ordinal,
                Modality = record.Modality,
                LocatorStart = record.LocatorStart,
                LocatorEnd = record.LocatorEnd,
                Content = record.Text
            });
        }

        return results;
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
