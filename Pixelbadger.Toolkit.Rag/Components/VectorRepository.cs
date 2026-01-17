namespace Pixelbadger.Toolkit.Rag.Components;

public interface IVectorRepository
{
    Task StoreVectorsAsync(string indexPath, string contentPath, List<IChunk> chunks);
    Task<List<SearchResult>> QueryVectorsAsync(string indexPath, string queryText, int maxResults, string[]? sourceIds);
    bool Exists(string indexPath);
}

public class VectorRepository : IVectorRepository
{
    private readonly IEmbeddingService _embeddingService;

    public VectorRepository(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    public async Task StoreVectorsAsync(string indexPath, string contentPath, List<IChunk> chunks)
    {
        var sourceId = Path.GetFileNameWithoutExtension(contentPath);
        var sourceFile = Path.GetFileName(contentPath);

        // Generate embeddings in batch for efficiency
        var chunkTexts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(chunkTexts);

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

    public async Task<List<SearchResult>> QueryVectorsAsync(string indexPath, string queryText, int maxResults, string[]? sourceIds)
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

    public bool Exists(string indexPath)
    {
        var vectorStore = new VectorStore(indexPath);
        return vectorStore.Exists();
    }
}