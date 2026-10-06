namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Settings of the image preprocessor (defaults match processor_config.json).</summary>
public sealed record ImagePreprocessorOptions
{
    /// <summary>Allowed soft-token budgets (<c>_SUPPORTED_SOFT_TOKENS</c> in the HF processor).</summary>
    public static readonly IReadOnlyList<int> SupportedSoftTokens = [70, 140, 280, 560, 1120];

    public int MaxSoftTokens { get; init; } = 280;
    public int PatchSize { get; init; } = 16;
    public int PoolingKernelSize { get; init; } = 3;

    /// <summary>Rotate/flip according to the EXIF orientation tag (HF <c>load_image</c> calls <c>ImageOps.exif_transpose</c>).</summary>
    public bool ApplyExifOrientation { get; init; } = true;

    /// <summary>Decode guard against decompression bombs (same order of magnitude as Pillow's limit).</summary>
    public long MaxSourcePixels { get; init; } = 178_956_970;

    public VisionPatchLayout Layout { get; init; } = VisionPatchLayout.Default;
}
