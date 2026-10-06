namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>
/// Builds <c>input_ids</c> sequences for the text model. Pure: no tokeniser, no ONNX.
/// The <c>&lt;bos&gt;</c>/<c>&lt;eos&gt;</c> framing is applied here, exactly once; the tokeniser is asked
/// for raw ids only (it ignores the post-processor template in tokenizer.json).
/// </summary>
public static class GemmaInputBuilder
{
    /// <summary><c>&lt;bos&gt; body &lt;eos&gt;</c>. A body longer than the context window is truncated (eos is kept).</summary>
    public static long[] BuildText(ReadOnlySpan<int> bodyIds, int maxTokens = GemmaSpecialTokens.MaxContextTokens)
    {
        if (maxTokens < 2) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        var bodyLength = Math.Min(bodyIds.Length, maxTokens - 2);
        var ids = new long[bodyLength + 2];
        ids[0] = GemmaSpecialTokens.Bos;
        for (int i = 0; i < bodyLength; i++) ids[i + 1] = bodyIds[i];
        ids[^1] = GemmaSpecialTokens.Eos;
        return ids;
    }

    /// <summary><c>&lt;bos&gt; &lt;|image&gt; &lt;|image|&gt;×rows &lt;image|&gt; &lt;eos&gt;</c></summary>
    public static long[] BuildImage(int softTokenRows)
        => BuildMedia(GemmaSpecialTokens.ImageBegin, GemmaSpecialTokens.ImagePlaceholder, GemmaSpecialTokens.ImageEnd, softTokenRows);

    /// <summary><c>&lt;bos&gt; &lt;|audio&gt; &lt;|audio|&gt;×rows &lt;audio|&gt; &lt;eos&gt;</c></summary>
    public static long[] BuildAudio(int softTokenRows)
        => BuildMedia(GemmaSpecialTokens.AudioBegin, GemmaSpecialTokens.AudioPlaceholder, GemmaSpecialTokens.AudioEnd, softTokenRows);

    private static long[] BuildMedia(int begin, int placeholder, int end, int rows)
    {
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows), "A media item needs at least one soft token.");
        if (rows + 4 > GemmaSpecialTokens.MaxContextTokens)
            throw new ArgumentOutOfRangeException(nameof(rows), $"{rows} soft tokens exceed the {GemmaSpecialTokens.MaxContextTokens}-token context.");

        var ids = new long[rows + 4];
        ids[0] = GemmaSpecialTokens.Bos;
        ids[1] = begin;
        Array.Fill(ids, (long)placeholder, 2, rows);
        ids[rows + 2] = end;
        ids[rows + 3] = GemmaSpecialTokens.Eos;
        return ids;
    }
}
