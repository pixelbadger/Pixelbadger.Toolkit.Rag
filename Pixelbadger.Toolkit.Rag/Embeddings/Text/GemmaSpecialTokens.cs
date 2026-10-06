namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>Special token ids of EmbeddingGemma 2 (reference §4).</summary>
public static class GemmaSpecialTokens
{
    public const int Pad = 0;
    public const int Eos = 1;
    public const int Bos = 2;

    public const int ImageBegin = 255999;
    public const int ImagePlaceholder = 258880;
    public const int ImageEnd = 258882;

    public const int AudioBegin = 256000;
    public const int AudioPlaceholder = 258881;
    public const int AudioEnd = 258883;

    public const int VideoPlaceholder = 258884;

    /// <summary>Total context shared across all modalities in one input.</summary>
    public const int MaxContextTokens = 8192;

    /// <summary>
    /// True for ids that carry framing or multimodal placeholders. Literal text that happens to look
    /// like one of these must never be turned into the id, otherwise the placeholder count would no
    /// longer match the feature rows supplied to the model.
    /// </summary>
    public static bool IsReserved(int id) => id is Pad or Eos or Bos
        or ImageBegin or ImagePlaceholder or ImageEnd
        or AudioBegin or AudioPlaceholder or AudioEnd or VideoPlaceholder;
}
