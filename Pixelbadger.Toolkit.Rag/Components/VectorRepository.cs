using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Vector repository backed by SQLite-vec for storing and searching chunk embeddings.
/// Generates embeddings for chunks during storage and for query text at search time.
/// </summary>
public class VectorRepository : IVectorRepository, IAsyncDisposable
{
    private const string CollectionName = "chunks";
    private readonly IEmbeddingService _embeddingService;
    private string? _currentIndexPath;
    private SqliteVectorStore? _vectorStore;
    private SqliteCollection<string, ChunkVectorRecord>? _collection;
    private bool _initialized;

    public VectorRepository(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    private string GetDatabasePath(string indexPath) => Path.Combine(indexPath, "vectors.db");

    /// <summary>
    /// Initializes the vector store and creates the collection if it doesn't exist.
    /// </summary>
    private async Task InitializeAsync(string indexPath, CancellationToken cancellationToken = default)
    {
        // If already initialized for this path, return
        if (_initialized && _currentIndexPath == indexPath)
            return;

        // Dispose existing resources if switching paths
        if (_initialized && _currentIndexPath != indexPath)
        {
            await DisposeAsync();
        }

        var databasePath = GetDatabasePath(indexPath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = $"Data Source={databasePath}";
        _vectorStore = new SqliteVectorStore(connectionString);
        _collection = new SqliteCollection<string, ChunkVectorRecord>(connectionString, CollectionName);
        await _collection.EnsureCollectionExistsAsync(cancellationToken);
        _currentIndexPath = indexPath;
        _initialized = true;
    }

    /// <summary>
    /// Stores chunks and generates embeddings for each chunk in the vector database.
    /// </summary>
    public async Task StoreVectorsAsync(string indexPath, string contentPath, List<IChunk> chunks)
    {
        var sourceId = Path.GetFileNameWithoutExtension(contentPath);
        var sourceFile = Path.GetFileName(contentPath);

        // Create vector records and generate embeddings for each chunk
        var records = new List<ChunkVectorRecord>();
        foreach (var chunk in chunks)
        {
            // Generate embedding for this chunk's content
            var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Content);

            var record = new ChunkVectorRecord
            {
                Key = $"{sourceId}_{chunk.ChunkNumber}",
                Content = chunk.Content,
                SourceFile = sourceFile,
                SourcePath = contentPath,
                SourceId = sourceId,
                ChunkNumber = chunk.ChunkNumber,
                DocumentId = $"{sourceFile}_{chunk.ChunkNumber}",
                Embedding = embedding
            };
            records.Add(record);
        }

        // Store in vector database
        await InitializeAsync(indexPath);
        await _collection!.UpsertAsync(records);
    }

    /// <summary>
    /// Searches for similar chunks by generating an embedding for the query text.
    /// </summary>
    public async Task<List<SearchResult>> QueryVectorsAsync(string indexPath, string queryText, int maxResults, string[]? sourceIds)
    {
        var databasePath = GetDatabasePath(indexPath);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"Vector database not found at {indexPath}. Ensure vectors were stored during ingest.");
        }

        await InitializeAsync(indexPath);

        // Generate embedding for the query text
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(queryText);

        // Build search options with filter if needed
        VectorSearchOptions<ChunkVectorRecord>? searchOptions = null;
        if (sourceIds != null && sourceIds.Length > 0)
        {
            searchOptions = new VectorSearchOptions<ChunkVectorRecord>
            {
                Filter = record => sourceIds.Contains(record.SourceId)
            };
        }

        // Search the vector store
        var searchResults = _collection!.SearchAsync(queryEmbedding, maxResults, searchOptions);

        var results = new List<SearchResult>();
        await foreach (var result in searchResults)
        {
            var distance = (float)(result.Score ?? 0);
            var similarity = 1.0f - (distance * distance) / 2.0f; // Convert Euclidean distance to cosine similarity for normalized vectors
            results.Add(new SearchResult
            {
                Score = similarity,
                Content = result.Record.Content,
                SourceFile = result.Record.SourceFile,
                SourcePath = result.Record.SourcePath,
                SourceId = result.Record.SourceId,
                ParagraphNumber = result.Record.ChunkNumber,
                DocumentId = result.Record.DocumentId
            });
        }

        return results;
    }

    public bool Exists(string indexPath)
    {
        var databasePath = GetDatabasePath(indexPath);
        return File.Exists(databasePath);
    }

    public ValueTask DisposeAsync()
    {
        _vectorStore = null;
        _collection = null;
        _initialized = false;
        _currentIndexPath = null;
        return ValueTask.CompletedTask;
    }
}