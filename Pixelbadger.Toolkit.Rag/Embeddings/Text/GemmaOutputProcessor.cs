namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>Turns the raw <c>sentence_embedding</c> output [batch, hidden] into stored vectors.</summary>
public static class GemmaOutputProcessor
{
    /// <summary>Per row: truncate to <paramref name="dimensions"/> and re-normalise (<see cref="EmbeddingMath"/>).</summary>
    public static float[][] Process(ReadOnlySpan<float> sentenceEmbedding, int batchSize, int dimensions)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (sentenceEmbedding.Length == 0 || sentenceEmbedding.Length % batchSize != 0)
            throw new InvalidOperationException($"sentence_embedding has {sentenceEmbedding.Length} values, not a multiple of batch size {batchSize}.");

        var hidden = sentenceEmbedding.Length / batchSize;
        if (hidden < dimensions)
            throw new InvalidOperationException($"sentence_embedding width {hidden} is smaller than the requested {dimensions} dimensions.");

        var result = new float[batchSize][];
        for (int i = 0; i < batchSize; i++)
            result[i] = EmbeddingMath.TruncateAndNormalize(sentenceEmbedding.Slice(i * hidden, hidden), dimensions);
        return result;
    }
}
