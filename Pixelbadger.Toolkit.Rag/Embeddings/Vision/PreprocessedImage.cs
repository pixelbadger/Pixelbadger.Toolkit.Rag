namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>
/// One image ready for vision_encoder_quantized.onnx (batch of 1).
/// PixelValues: row-major [1, NumPatches, 768] (16x16x3 patch flattened).
/// PositionIds: row-major [1, NumPatches, 2] patch grid coordinates.
/// Expected soft tokens = (Height/48) * (Width/48).
/// </summary>
public sealed record PreprocessedImage(float[] PixelValues, long[] PositionIds, int NumPatches, int Height, int Width)
{
    public int ExpectedSoftTokens => (Height / 48) * (Width / 48);
}
