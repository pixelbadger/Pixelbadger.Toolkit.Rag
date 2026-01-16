using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Data model for storing chunk embeddings in the vector store.
/// </summary>
public class ChunkVectorRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceFile { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourcePath { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceId { get; set; } = string.Empty;

    [VectorStoreData]
    public int ChunkNumber { get; set; }

    [VectorStoreData]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreVector(OpenAIEmbeddingService.EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// Vector store backed by SQLite-vec for storing and searching chunk embeddings.
/// </summary>
public class VectorStore : IAsyncDisposable
{
    private const string CollectionName = "chunks";
    private readonly string _databasePath;
    private SqliteVectorStore? _vectorStore;
    private SqliteCollection<string, ChunkVectorRecord>? _collection;
    private bool _initialized;

    public VectorStore(string indexPath)
    {
        _databasePath = Path.Combine(indexPath, "vectors.db");
    }

    /// <summary>
    /// Initializes the vector store and creates the collection if it doesn't exist.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = $"Data Source={_databasePath}";
        _vectorStore = new SqliteVectorStore(connectionString);
        _collection = new SqliteCollection<string, ChunkVectorRecord>(connectionString, CollectionName);
        await _collection.EnsureCollectionExistsAsync(cancellationToken);
        _initialized = true;
    }

    /// <summary>
    /// Upserts a single chunk embedding into the vector store.
    /// </summary>
    public async Task UpsertChunkAsync(ChunkVectorRecord record, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _collection!.UpsertAsync(record, cancellationToken);
    }

    /// <summary>
    /// Upserts multiple chunk embeddings into the vector store in batch.
    /// </summary>
    public async Task UpsertChunksBatchAsync(IEnumerable<ChunkVectorRecord> records, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _collection!.UpsertAsync(records, cancellationToken);
    }

    /// <summary>
    /// Searches for similar chunks using cosine similarity.
    /// </summary>
    /// <param name="embedding">The query embedding vector.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="sourceIds">Optional source IDs to filter results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results ordered by similarity score (descending).</returns>
    public async Task<List<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> embedding,
        int maxResults = 10,
        string[]? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        VectorSearchOptions<ChunkVectorRecord>? searchOptions = null;

        // Apply source ID filter if provided
        if (sourceIds != null && sourceIds.Length > 0)
        {
            searchOptions = new VectorSearchOptions<ChunkVectorRecord>
            {
                Filter = record => sourceIds.Contains(record.SourceId)
            };
        }

        var searchResults = _collection!.SearchAsync(embedding, maxResults, searchOptions, cancellationToken);

        var results = new List<SearchResult>();
        await foreach (var result in searchResults.WithCancellation(cancellationToken))
        {
            results.Add(new SearchResult
            {
                Score = (float)(result.Score ?? 0),
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

    /// <summary>
    /// Checks if the vector store database exists.
    /// </summary>
    public bool Exists()
    {
        return File.Exists(_databasePath);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("VectorStore has not been initialized. Call InitializeAsync first.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _vectorStore = null;
        _collection = null;
        _initialized = false;
        return ValueTask.CompletedTask;
    }
}
