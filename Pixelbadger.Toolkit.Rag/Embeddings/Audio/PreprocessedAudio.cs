namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>
/// One audio window ready for audio_encoder.onnx.
/// InputFeatures: row-major [Clips, Frames, 128] log-mel.
/// Mask: row-major [Clips, Frames], true for real (non-padding) frames.
/// </summary>
public sealed record PreprocessedAudio(float[] InputFeatures, bool[] Mask, int Clips, int Frames)
{
    public const int MelBins = 128;
}

/// <summary>A ~30 s window of a longer recording; each window becomes one chunk.</summary>
public sealed record AudioWindow(long StartMs, long EndMs, PreprocessedAudio Features);
