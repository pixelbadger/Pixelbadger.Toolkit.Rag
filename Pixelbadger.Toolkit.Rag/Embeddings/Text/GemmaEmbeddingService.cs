using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

// STUB (Phase 0). Implemented by workstream A.
public sealed class GemmaEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingModelOptions _options;

    public GemmaEmbeddingService(EmbeddingModelOptions options, OnnxSessionProvider sessions, IVisionEncoder visionEncoder, IAudioEncoder audioEncoder)
    {
        _options = options;
    }

    public int Dimensions => _options.Dimensions;
    public string ModelId => _options.ModelId;

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<float[]>> EmbedDocumentTextAsync(string? title, IReadOnlyList<string> chunks, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<float[]> EmbedImageAsync(PreprocessedImage image, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<float[]> EmbedAudioAsync(PreprocessedAudio audio, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
