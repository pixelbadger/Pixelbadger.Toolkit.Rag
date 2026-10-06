namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>Runs audio_encoder.onnx. Session is loaded lazily on first use.</summary>
public interface IAudioEncoder
{
    EncodedFeatures Encode(PreprocessedAudio audio);
}
