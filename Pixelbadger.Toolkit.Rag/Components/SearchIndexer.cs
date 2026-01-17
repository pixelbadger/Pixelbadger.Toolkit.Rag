using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Pixelbadger.Toolkit.Rag.Components;

public class SearchResult
{
    public float Score { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int ParagraphNumber { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
}

/// <summary>
/// Options for content ingestion.
/// </summary>
public class IngestOptions
{
    /// <summary>
    /// Enable vector storage using sqlite-vec alongside Lucene BM25 indexing.
    /// </summary>
    public bool EnableVectorStorage { get; set; }
}

/// <summary>
/// Search mode for queries.
/// </summary>
public enum SearchMode
{
    /// <summary>
    /// BM25 keyword search using Lucene.
    /// </summary>
    Bm25,

    /// <summary>
    /// Vector similarity search using embeddings.
    /// </summary>
    Vector,

    /// <summary>
    /// Hybrid search combining BM25 and vector search using Reciprocal Rank Fusion.
    /// </summary>
    Hybrid
}

public class SearchIndexer
{
    private const LuceneVersion LUCENE_VERSION = LuceneVersion.LUCENE_48;

    private readonly IEmbeddingService _embeddingService;

    public IEmbeddingService EmbeddingService => _embeddingService;

    public SearchIndexer(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
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
        await IndexWithLuceneAsync(indexPath, contentPath, nonEmptyChunks);

        // Vector storage (always enabled for eval harness)
        await StoreVectorsAsync(indexPath, contentPath, nonEmptyChunks);
    }

    private async Task IndexWithLuceneAsync(string indexPath, string contentPath, List<IChunk> chunks)
    {
        var indexDirectory = FSDirectory.Open(indexPath);
        var analyzer = new StandardAnalyzer(LUCENE_VERSION);
        var config = new IndexWriterConfig(LUCENE_VERSION, analyzer);

        // Ensure consistent BM25 similarity for both indexing and searching
        config.Similarity = new BM25Similarity();

        using var writer = new IndexWriter(indexDirectory, config);

        var sourceId = Path.GetFileNameWithoutExtension(contentPath);

        foreach (var chunk in chunks)
        {
            var doc = new Document();

            // Add the chunk content as a searchable field
            doc.Add(new TextField("content", chunk.Content, Field.Store.YES));

            // Add metadata fields
            doc.Add(new StringField("source_file", Path.GetFileName(contentPath), Field.Store.YES));
            doc.Add(new StringField("source_path", contentPath, Field.Store.YES));
            doc.Add(new StringField("source_id", sourceId, Field.Store.YES));
            doc.Add(new Int32Field("paragraph_number", chunk.ChunkNumber, Field.Store.YES));
            doc.Add(new StringField("document_id", $"{Path.GetFileName(contentPath)}_{chunk.ChunkNumber}", Field.Store.YES));

            writer.AddDocument(doc);
        }

        writer.Commit();
        writer.Dispose();
        indexDirectory.Dispose();
        analyzer.Dispose();
    }

    private async Task StoreVectorsAsync(string indexPath, string contentPath, List<IChunk> chunks)
    {
        var sourceId = Path.GetFileNameWithoutExtension(contentPath);
        var sourceFile = Path.GetFileName(contentPath);

        // Generate embeddings in batch for efficiency
        var chunkTexts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService!.GenerateEmbeddingsAsync(chunkTexts);

        // Create vector records
        var records = new List<ChunkVectorRecord>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var record = new ChunkVectorRecord
            {
                Key = $"{sourceId}_{chunk.ChunkNumber}",
                Content = chunk.Content,
                SourceFile = sourceFile,
                SourcePath = contentPath,
                SourceId = sourceId,
                ChunkNumber = chunk.ChunkNumber,
                DocumentId = $"{sourceFile}_{chunk.ChunkNumber}",
                Embedding = embeddings[i]
            };
            records.Add(record);
        }

        // Store in vector database
        await using var vectorStore = new VectorStore(indexPath);
        await vectorStore.InitializeAsync();
        await vectorStore.UpsertChunksBatchAsync(records);
    }

    public Task<List<SearchResult>> QueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        if (!System.IO.Directory.Exists(indexPath))
        {
            throw new DirectoryNotFoundException($"Index directory not found: {indexPath}");
        }

        var results = new List<SearchResult>();
        var indexDirectory = FSDirectory.Open(indexPath);
        var analyzer = new StandardAnalyzer(LUCENE_VERSION);

        using var reader = DirectoryReader.Open(indexDirectory);
        var searcher = new IndexSearcher(reader);

        // Use BM25 similarity to match indexing configuration
        searcher.Similarity = new BM25Similarity();

        var parser = new QueryParser(LUCENE_VERSION, "content", analyzer);
        var contentQuery = parser.Parse(queryText);

        Query finalQuery;
        if (sourceIds != null && sourceIds.Length > 0)
        {
            // Create a boolean query to combine content search with source ID filter
            var boolQuery = new BooleanQuery();
            boolQuery.Add(contentQuery, Occur.MUST);

            // Add source ID filter as OR terms within a nested boolean query
            var sourceIdQuery = new BooleanQuery();
            foreach (var sourceId in sourceIds)
            {
                var termQuery = new TermQuery(new Term("source_id", sourceId));
                sourceIdQuery.Add(termQuery, Occur.SHOULD);
            }
            boolQuery.Add(sourceIdQuery, Occur.MUST);
            finalQuery = boolQuery;
        }
        else
        {
            finalQuery = contentQuery;
        }

        var hits = searcher.Search(finalQuery, maxResults);

        foreach (var scoreDoc in hits.ScoreDocs)
        {
            var doc = searcher.Doc(scoreDoc.Doc);
            var result = new SearchResult
            {
                Score = scoreDoc.Score,
                Content = doc.Get("content") ?? string.Empty,
                SourceFile = doc.Get("source_file") ?? string.Empty,
                SourcePath = doc.Get("source_path") ?? string.Empty,
                SourceId = doc.Get("source_id") ?? string.Empty,
                ParagraphNumber = int.Parse(doc.Get("paragraph_number") ?? "0"),
                DocumentId = doc.Get("document_id") ?? string.Empty
            };
            results.Add(result);
        }

        reader.Dispose();
        indexDirectory.Dispose();
        analyzer.Dispose();

        return Task.FromResult(results);
    }

    /// <summary>
    /// Performs vector similarity search using embeddings.
    /// </summary>
    public async Task<List<SearchResult>> VectorQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        var vectorStore = new VectorStore(indexPath);
        if (!vectorStore.Exists())
        {
            throw new FileNotFoundException($"Vector database not found at {indexPath}. Ensure vectors were stored during ingest.");
        }

        await vectorStore.InitializeAsync();

        // Generate embedding for the query
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(queryText);

        // Search the vector store
        var results = await vectorStore.SearchAsync(queryEmbedding, maxResults, sourceIds);

        await vectorStore.DisposeAsync();

        return results;
    }

    /// <summary>
    /// Performs search using the specified mode.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string indexPath, string queryText, SearchMode mode, int maxResults = 10, string[]? sourceIds = null)
    {
        return mode switch
        {
            SearchMode.Bm25 => await QueryAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Vector => await VectorQueryAsync(indexPath, queryText, maxResults, sourceIds),
            SearchMode.Hybrid => await HybridQueryAsync(indexPath, queryText, maxResults, sourceIds),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown search mode")
        };
    }

    /// <summary>
    /// Performs hybrid search combining BM25 and vector search using Reciprocal Rank Fusion (RRF).
    /// </summary>
    public async Task<List<SearchResult>> HybridQueryAsync(string indexPath, string queryText, int maxResults = 10, string[]? sourceIds = null)
    {
        // Fetch more results from each search to improve fusion quality
        var fetchCount = Math.Max(maxResults * 2, 20);

        // Run both searches in parallel
        var bm25Task = QueryAsync(indexPath, queryText, fetchCount, sourceIds);
        var vectorTask = VectorQueryAsync(indexPath, queryText, fetchCount, sourceIds);

        await Task.WhenAll(bm25Task, vectorTask);

        var bm25Results = bm25Task.Result;
        var vectorResults = vectorTask.Result;

        // Apply Reciprocal Rank Fusion (RRF)
        // RRF score = sum(1 / (k + rank)) for each result list
        const int k = 60; // Standard RRF constant

        var fusedScores = new Dictionary<string, (float Score, SearchResult Result)>();

        // Process BM25 results
        for (int i = 0; i < bm25Results.Count; i++)
        {
            var result = bm25Results[i];
            var rrfScore = 1.0f / (k + i + 1);

            if (fusedScores.TryGetValue(result.DocumentId, out var existing))
            {
                fusedScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
            }
            else
            {
                fusedScores[result.DocumentId] = (rrfScore, result);
            }
        }

        // Process vector results
        for (int i = 0; i < vectorResults.Count; i++)
        {
            var result = vectorResults[i];
            var rrfScore = 1.0f / (k + i + 1);

            if (fusedScores.TryGetValue(result.DocumentId, out var existing))
            {
                fusedScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
            }
            else
            {
                fusedScores[result.DocumentId] = (rrfScore, result);
            }
        }

        // Sort by fused score and return top results
        return fusedScores.Values
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x =>
            {
                x.Result.Score = x.Score;
                return x.Result;
            })
            .ToList();
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
