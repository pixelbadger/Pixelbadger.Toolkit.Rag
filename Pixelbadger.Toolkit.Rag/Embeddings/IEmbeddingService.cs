using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Embeddings;

/// <summary>
/// Produces embeddings in a single shared text/image/audio space. Every returned vector has
/// length <see cref="Dimensions"/> and unit L2 norm (truncated + re-normalised in one place:
/// <see cref="EmbeddingMath.TruncateAndNormalize"/>).
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Output dimensionality (256).</summary>
    int Dimensions { get; }

    /// <summary>Model identifier stored on each chunk, e.g. "embeddinggemma-2-q8@256".</summary>
    string ModelId { get; }

    /// <summary>Embeds a search query using the "task: search result | query: {q}" prompt.</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds document text chunks using the "title: {title|none} | text: {chunk}" prompt.
    /// Returns one vector per chunk, in order.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentTextAsync(string? title, IReadOnlyList<string> chunks, CancellationToken cancellationToken = default);

    /// <summary>Embeds one preprocessed image (no text prefix).</summary>
    Task<float[]> EmbedImageAsync(PreprocessedImage image, CancellationToken cancellationToken = default);

    /// <summary>Embeds one preprocessed audio window (no text prefix).</summary>
    Task<float[]> EmbedAudioAsync(PreprocessedAudio audio, CancellationToken cancellationToken = default);
}
