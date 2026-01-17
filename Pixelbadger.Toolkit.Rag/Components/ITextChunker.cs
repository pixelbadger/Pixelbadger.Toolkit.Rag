using Microsoft.Extensions.AI;
using SemanticChunkerNET;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IChunk
{
    string Content { get; }
    int ChunkNumber { get; }
    ReadOnlyMemory<float> Embedding { get; }
}

public interface ITextChunker
{
    Task<List<IChunk>> ChunkTextAsync(string content);
}

public class SemanticChunkWrapper : IChunk
{
    private readonly Chunk _chunk;
    private readonly int _chunkNumber;

    public SemanticChunkWrapper(Chunk chunk, int chunkNumber)
    {
        _chunk = chunk;
        _chunkNumber = chunkNumber;
    }

    public string Content => _chunk.Text;
    public int ChunkNumber => _chunkNumber;
    public ReadOnlyMemory<float> Embedding => _chunk.Embedding.Vector;
    public Chunk OriginalChunk => _chunk;
}

public class SemanticTextChunker : ITextChunker
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly int _tokenLimit;
    private readonly int _bufferSize;
    private readonly BreakpointThresholdType _thresholdType;
    private readonly double _thresholdAmount;

    public SemanticTextChunker(
        string? apiKey = null,
        int tokenLimit = 512,
        int bufferSize = 1,
        BreakpointThresholdType thresholdType = BreakpointThresholdType.Percentile,
        double thresholdAmount = 95)
    {
        // Get API key from environment if not provided
        apiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");
        }

        // Create OpenAI embedding generator using Microsoft.Extensions.AI
        var openAIClient = new OpenAI.OpenAIClient(apiKey);
        var embeddingClient = openAIClient.GetEmbeddingClient("text-embedding-3-large");
        _embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();

        _tokenLimit = tokenLimit;
        _bufferSize = bufferSize;
        _thresholdType = thresholdType;
        _thresholdAmount = thresholdAmount;
    }

    public async Task<List<IChunk>> ChunkTextAsync(string content)
    {
        var semanticChunker = new SemanticChunker(
            _embeddingGenerator,
            tokenLimit: _tokenLimit,
            bufferSize: _bufferSize,
            thresholdType: _thresholdType,
            thresholdAmount: _thresholdAmount
        );

        var chunks = await semanticChunker.CreateChunksAsync(content);

        return chunks.Select((chunk, index) => (IChunk)new SemanticChunkWrapper(chunk, index + 1)).ToList();
    }
}
