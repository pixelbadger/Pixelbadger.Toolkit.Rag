namespace Pixelbadger.Toolkit.Rag.Embeddings;

public static class EmbeddingMath
{
    /// <summary>
    /// Takes the first <paramref name="dimensions"/> values of <paramref name="source"/> and
    /// re-normalises them to unit L2 length (Matryoshka truncation). The single place this happens.
    /// </summary>
    public static float[] TruncateAndNormalize(ReadOnlySpan<float> source, int dimensions)
    {
        if (dimensions <= 0 || dimensions > source.Length)
            throw new ArgumentOutOfRangeException(nameof(dimensions));

        var slice = source[..dimensions];
        double sumSquares = 0;
        foreach (var x in slice) sumSquares += (double)x * x;
        var norm = (float)Math.Sqrt(sumSquares);
        if (norm == 0 || float.IsNaN(norm))
            throw new InvalidOperationException("Embedding has zero or NaN norm");

        var result = new float[dimensions];
        for (int i = 0; i < dimensions; i++) result[i] = slice[i] / norm;
        return result;
    }
}
