namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>Decodes an audio file (ffmpeg → 16 kHz mono f32) and builds windows of encoder inputs.</summary>
public interface IAudioPreprocessor
{
    Task<IReadOnlyList<AudioWindow>> PreprocessAsync(string filePath, CancellationToken cancellationToken = default);
}
