using Microsoft.Extensions.AI;

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
        // Create a 10-dimensional embedding (simplified vs real 3072-dimensional embeddings)
        var embedding = new float[10];

        if (string.IsNullOrWhiteSpace(text))
        {
            return embedding;
        }

        // Dimension 0-2: Based on word count (for semantic similarity)
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        embedding[0] = (float)wordCount / 10f;
        embedding[1] = (float)Math.Sin(wordCount);
        embedding[2] = (float)Math.Cos(wordCount);

        // Dimension 3-5: Based on character length
        embedding[3] = (float)text.Length / 100f;
        embedding[4] = (float)Math.Sin(text.Length);
        embedding[5] = (float)Math.Cos(text.Length);

        // Dimension 6-7: Based on first character (for sentence variation)
        var firstChar = char.ToLowerInvariant(text.Trim()[0]);
        embedding[6] = (float)firstChar / 127f;
        embedding[7] = (float)Math.Sin(firstChar);

        // Dimension 8-9: Based on last character
        var lastChar = char.ToLowerInvariant(text.Trim()[^1]);
        embedding[8] = (float)lastChar / 127f;
        embedding[9] = (float)Math.Cos(lastChar);

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
