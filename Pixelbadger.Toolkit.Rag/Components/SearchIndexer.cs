using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Pixelbadger.Toolkit.Rag.Components;

public class SearchIndexer
{
    private readonly ILuceneRepository _luceneRepo;
    private readonly IVectorRepository _vectorRepo;
    private readonly IReranker _reranker;

    public SearchIndexer(ILuceneRepository luceneRepo, IVectorRepository vectorRepo, IReranker reranker)
    {
        _luceneRepo = luceneRepo;
        _vectorRepo = vectorRepo;
        _reranker = reranker;
    }

    public Task IngestContentAsync(string indexPath, string contentPath, string? chunkingStrategy = null)
    {
        return IngestContentAsync(indexPath, contentPath, chunkingStrategy, null);
    }

    public async Task IngestContentAsync(string indexPath, string contentPath, string? chunkingStrategy, IngestOptions? options)
    {
        if (!File.Exists(contentPath))
        {
            throw new FileNotFoundException($"Content file not found: {contentPath}");
        }

        options ??= new IngestOptions();

        var content = await File.ReadAllTextAsync(contentPath);
        var chunks = await GetChunksForFileAsync(contentPath, content, chunkingStrategy);

        // Filter out empty chunks
        var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();

        // Lucene BM25 indexing
        await _luceneRepo.IndexWithLuceneAsync(indexPath, contentPath, nonEmptyChunks);

        // Vector storage (always enabled for eval harness)
        await _vectorRepo.StoreVectorsAsync(indexPath, contentPath, nonEmptyChunks);
    }

    /// <summary>
    /// Performs search using the specified mode.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string indexPath, string queryText, SearchMode mode, int maxResults = 10, string[]? sourceIds = null)
    {
        return mode switch
        {
            SearchMode.Bm25 => await _luceneRepo.QueryLuceneAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Vector => await VectorQueryAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Hybrid => await HybridQueryAsync(indexPath, queryText, maxResults, sourceIds),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown search mode")
        };
    }

    /// <summary>
    /// Performs vector similarity search using embeddings.
    /// </summary>
    public async Task<List<SearchResult>> VectorQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        return await _vectorRepo.QueryVectorsAsync(indexPath, queryText, maxResults, sourceIds);
    }

    /// <summary>
    /// Performs hybrid search combining BM25 and vector search using Reciprocal Rank Fusion.
    /// </summary>
    public async Task<List<SearchResult>> HybridQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
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

    private static async Task<List<IChunk>> GetChunksForFileAsync(string filePath, string content, string? chunkingStrategy = null)
    {
        ITextChunker chunker;

        // If explicit strategy is provided, use it
        if (!string.IsNullOrEmpty(chunkingStrategy))
        {
            chunker = chunkingStrategy.ToLowerInvariant() switch
            {
                "semantic" => new SemanticTextChunker(),
                "markdown" => new MarkdownTextChunker(),
                "paragraph" => new ParagraphTextChunker(),
                _ => throw new ArgumentException($"Unknown chunking strategy: {chunkingStrategy}")
            };
        }
        else
        {
            // Auto-detect based on file extension
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            chunker = extension switch
            {
                ".md" or ".markdown" => new MarkdownTextChunker(),
                _ => new ParagraphTextChunker()
            };
        }

        return await chunker.ChunkTextAsync(content);
    }
}