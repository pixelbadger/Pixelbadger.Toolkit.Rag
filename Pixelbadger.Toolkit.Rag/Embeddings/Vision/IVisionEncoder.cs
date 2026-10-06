namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Runs vision_encoder.onnx. Session is loaded lazily on first use.</summary>
public interface IVisionEncoder
{
    EncodedFeatures Encode(PreprocessedImage image);
}
