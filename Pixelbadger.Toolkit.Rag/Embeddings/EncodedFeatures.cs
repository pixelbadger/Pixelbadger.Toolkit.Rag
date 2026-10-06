namespace Pixelbadger.Toolkit.Rag.Embeddings;

/// <summary>
/// Encoder output fed into the text model: row-major [Rows, 512] float32.
/// Rows must equal the number of placeholder tokens expanded for this media item.
/// </summary>
public sealed record EncodedFeatures(float[] Data, int Rows)
{
    public const int FeatureWidth = 512;
}
