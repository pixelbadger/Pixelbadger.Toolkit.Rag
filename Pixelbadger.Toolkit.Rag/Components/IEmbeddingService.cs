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
