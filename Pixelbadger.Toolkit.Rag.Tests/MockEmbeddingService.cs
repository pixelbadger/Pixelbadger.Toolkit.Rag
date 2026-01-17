using Microsoft.Extensions.AI;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

/// <summary>
/// Mock embedding generator for testing that generates deterministic embeddings based on text content
/// </summary>
public class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata => new("mock-embedding-model");

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = new List<Embedding<float>>();

        foreach (var text in values)
        {
            // Generate a deterministic embedding based on text content
            var embedding = GenerateDeterministicEmbedding(text);
            embeddings.Add(new Embedding<float>(embedding));
        }

        return await Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // No resources to dispose
    }

    private static float[] GenerateDeterministicEmbedding(string text)
    {
        // Create a 3072-dimensional embedding (same as OpenAI)
        const int dimensions = 3072;
        var embedding = new float[dimensions];

        if (string.IsNullOrWhiteSpace(text))
        {
            return embedding;
        }

        // Generate deterministic values based on text
        var hash = text.GetHashCode();
        var random = new Random(hash);

        for (int i = 0; i < dimensions; i++)
        {
            embedding[i] = (float)random.NextDouble() * 2 - 1; // Random values between -1 and 1
        }

        // Normalize the embedding
        var magnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= magnitude;
            }
        }

        return embedding;
    }
}

/// <summary>
/// Mock embedding service for testing that wraps the generator
/// </summary>
public class MockEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public int Dimensions => 3072;

    public MockEmbeddingService()
    {
        _generator = new MockEmbeddingGenerator();
    }

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await _generator.GenerateAsync(new[] { text }, cancellationToken: cancellationToken);
        return result[0].Vector;
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var result = await _generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        return result.Select(e => e.Vector).ToList();
    }
}
