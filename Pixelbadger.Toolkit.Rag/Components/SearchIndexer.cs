using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public class SearchIndexer
{
    private readonly ILuceneRepository _luceneRepo;
    private readonly IVectorRepository _vectorRepo;
    private readonly IReranker _reranker;
    private readonly ITextChunker _chunker;
    private readonly FileReaderFactory _fileReaderFactory;

    public SearchIndexer(ILuceneRepository luceneRepo, IVectorRepository vectorRepo, IReranker reranker, ITextChunker chunker, FileReaderFactory fileReaderFactory)
    {
        _luceneRepo = luceneRepo;
        _vectorRepo = vectorRepo;
        _reranker = reranker;
        _chunker = chunker;
        _fileReaderFactory = fileReaderFactory;
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
    /// Ingests all supported files from a folder into the search index.
    /// </summary>
    /// <param name="indexPath">Path to the Lucene index directory.</param>
    /// <param name="folderPath">Path to the folder containing files to ingest.</param>
    /// <param name="chunkingStrategy">Optional chunking strategy (currently unused).</param>
    /// <param name="options">Optional ingestion options.</param>
    public async Task IngestFolderAsync(string indexPath, string folderPath, string? chunkingStrategy = null, IngestOptions? options = null)
    {
        if (!System.IO.Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        options ??= new IngestOptions();

        // Discover all files in the folder
        var allFiles = System.IO.Directory.GetFiles(folderPath, "*.*", System.IO.SearchOption.AllDirectories);

        // Filter to only supported file types
        var supportedFiles = allFiles.Where(file => _fileReaderFactory.CanRead(file)).ToList();

        if (supportedFiles.Count == 0)
        {
            Console.WriteLine($"No supported files found in {folderPath}");
            Console.WriteLine($"Supported extensions: {string.Join(", ", _fileReaderFactory.SupportedExtensions)}");
            return;
        }

        Console.WriteLine($"Found {supportedFiles.Count} supported files to ingest");

        // Process each file
        foreach (var filePath in supportedFiles)
        {
            try
            {
                Console.WriteLine($"Ingesting: {Path.GetFileName(filePath)}");

                // Get the appropriate reader for this file
                var reader = _fileReaderFactory.GetReader(filePath);

                // Read the content using the file reader
                var content = await reader.ReadTextAsync(filePath);

                // Chunk the content
                var chunks = await GetChunksForFileAsync(filePath, content, chunkingStrategy);

                // Filter out empty chunks
                var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();

                if (nonEmptyChunks.Count == 0)
                {
                    Console.WriteLine($"  Skipped (no content): {Path.GetFileName(filePath)}");
                    continue;
                }

                // Lucene BM25 indexing
                await _luceneRepo.IndexWithLuceneAsync(indexPath, filePath, nonEmptyChunks);

                // Vector storage
                await _vectorRepo.StoreVectorsAsync(indexPath, filePath, nonEmptyChunks);

                Console.WriteLine($"  Indexed {nonEmptyChunks.Count} chunks from {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error ingesting {Path.GetFileName(filePath)}: {ex.Message}");
                // Continue processing other files
            }
        }

        Console.WriteLine($"Completed ingestion of {supportedFiles.Count} files");
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

    private async Task<List<IChunk>> GetChunksForFileAsync(string filePath, string content, string? chunkingStrategy = null)
    {
        return await _chunker.ChunkTextAsync(content);
    }
}