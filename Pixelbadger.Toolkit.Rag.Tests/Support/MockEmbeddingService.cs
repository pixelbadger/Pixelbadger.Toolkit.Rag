using System.Security.Cryptography;
using System.Text;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

/// <summary>
/// Deterministic 256-d unit vectors derived from a SHA-256 of the input. Queries and documents
/// with identical text map to identical vectors, so tests can force a vector hit.
/// No model files needed.
/// </summary>
public sealed class MockEmbeddingService : IEmbeddingService
{
    public int Dimensions => 256;
    public string ModelId => "mock@256";

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult(Vector("text:" + query));

    public Task<IReadOnlyList<float[]>> EmbedDocumentTextAsync(string? title, IReadOnlyList<string> chunks, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<float[]>>(chunks.Select(c => Vector("text:" + c)).ToList());

    public Task<float[]> EmbedImageAsync(PreprocessedImage image, CancellationToken cancellationToken = default)
        => Task.FromResult(Vector("image:" + Hash(image.PixelValues)));

    public Task<float[]> EmbedAudioAsync(PreprocessedAudio audio, CancellationToken cancellationToken = default)
        => Task.FromResult(Vector("audio:" + Hash(audio.InputFeatures)));

    public static float[] Vector(string seed)
    {
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0));
        var raw = new float[256];
        for (int i = 0; i < raw.Length; i++) raw[i] = (float)(random.NextDouble() * 2 - 1);
        return EmbeddingMath.TruncateAndNormalize(raw, 256);
    }

    private static string Hash(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
