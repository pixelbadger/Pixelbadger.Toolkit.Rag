using Microsoft.Extensions.AI;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Interface for embedding generation services.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// The dimensionality of the embeddings produced by this service.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Generates an embedding for a single text input.
    /// </summary>
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple text inputs in batch.
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenAI embedding service using text-embedding-3-large (3072 dimensions).
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public const int EmbeddingDimensions = 3072;

    public int Dimensions => EmbeddingDimensions;

    public OpenAIEmbeddingService(string? apiKey = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");
        }

        var openAIClient = new OpenAI.OpenAIClient(apiKey);
        var embeddingClient = openAIClient.GetEmbeddingClient("text-embedding-3-large");
        _embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();
    }

    public OpenAIEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
    }

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddingGenerator.GenerateAsync(text, cancellationToken: cancellationToken);
        return embedding.Vector;
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
        {
            return Array.Empty<ReadOnlyMemory<float>>();
        }

        var embeddings = await _embeddingGenerator.GenerateAsync(textList, cancellationToken: cancellationToken);
        return embeddings.Select(e => e.Vector).ToList();
    }
}
