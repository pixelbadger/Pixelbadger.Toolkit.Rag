namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Target-size rule of the Gemma 4 / EmbeddingGemma 2 image processor.</summary>
public static class AspectRatioSizing
{
    /// <summary>
    /// Direct port of <c>get_aspect_ratio_preserving_size</c> from
    /// transformers/models/gemma4/image_processing_pil_gemma4.py (transformers 5.19.0), which
    /// EmbeddingGemma2Processor also imports. Returns the largest (height, width) that keeps the aspect ratio,
    /// has at most <paramref name="maxPatches"/> patches and is divisible by patchSize * poolingKernelSize.
    /// <paramref name="maxPatches"/> = max_soft_tokens * poolingKernelSize^2.
    /// </summary>
    public static (int Height, int Width) GetAspectRatioPreservingSize(int height, int width, int patchSize, int maxPatches, int poolingKernelSize)
    {
        if (height <= 0 || width <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Image dimensions must be positive.");

        double totalPx = (double)height * width;
        double targetPx = (double)maxPatches * patchSize * patchSize;
        double factor = Math.Sqrt(targetPx / totalPx);
        double idealHeight = factor * height;
        double idealWidth = factor * width;
        int sideMult = poolingKernelSize * patchSize;

        int targetHeight = (int)Math.Floor(idealHeight / sideMult) * sideMult;
        int targetWidth = (int)Math.Floor(idealWidth / sideMult) * sideMult;

        if (targetHeight == 0 && targetWidth == 0)
            throw new InvalidDataException($"Cannot resize {width}x{height} image to a 0x0 target (must be divisible by {sideMult}).");

        int maxSideLength = maxPatches / (poolingKernelSize * poolingKernelSize) * sideMult;
        if (targetHeight == 0)
        {
            targetHeight = sideMult;
            targetWidth = (int)Math.Min(Math.Floor((double)width / height) * sideMult, maxSideLength);
        }
        else if (targetWidth == 0)
        {
            targetWidth = sideMult;
            targetHeight = (int)Math.Min(Math.Floor((double)height / width) * sideMult, maxSideLength);
        }

        if ((double)targetHeight * targetWidth > targetPx)
            throw new InvalidDataException($"Resizing {height}x{width} to {targetHeight}x{targetWidth} exceeds {maxPatches} patches.");

        return (targetHeight, targetWidth);
    }
}
