using SkiaSharp;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>
/// Image file to vision-encoder tensors, reproducing the Hugging Face Gemma 4 / EmbeddingGemma 2 image processor:
/// decode (EXIF-oriented), drop alpha (PIL <c>convert("RGB")</c>), aspect-preserving resize to dims divisible by
/// 48 within the soft-token budget (bicubic, ported from PIL), rescale by 1/255, no normalisation, patchify.
/// SkiaSharp is used for decoding only.
/// </summary>
public sealed class ImagePreprocessor : IImagePreprocessor
{
    private readonly ImagePreprocessorOptions _options;

    public ImagePreprocessor() : this(new ImagePreprocessorOptions()) { }

    public ImagePreprocessor(ImagePreprocessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ImagePreprocessorOptions.SupportedSoftTokens.Contains(options.MaxSoftTokens))
            throw new ArgumentException($"MaxSoftTokens must be one of {string.Join("/", ImagePreprocessorOptions.SupportedSoftTokens)}, got {options.MaxSoftTokens}.", nameof(options));
        if (options.PatchSize <= 0 || options.PoolingKernelSize <= 0)
            throw new ArgumentException("PatchSize and PoolingKernelSize must be positive.", nameof(options));
        _options = options;
    }

    public async Task<PreprocessedImage> PreprocessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Image file not found: {filePath}", filePath);

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return await Task.Run(() => Preprocess(bytes), cancellationToken);
    }

    /// <summary>Preprocesses an encoded image (PNG, JPEG, WebP, GIF, BMP, ...).</summary>
    public PreprocessedImage Preprocess(byte[] encodedImage)
    {
        var (rgb, width, height) = Decode(encodedImage);
        return PreprocessRgb(rgb, width, height);
    }

    /// <summary>Preprocesses already-decoded interleaved RGB pixels (resize, rescale, patchify).</summary>
    public PreprocessedImage PreprocessRgb(byte[] rgb, int width, int height)
    {
        var (targetH, targetW) = AspectRatioSizing.GetAspectRatioPreservingSize(
            height, width, _options.PatchSize, _options.MaxSoftTokens * _options.PoolingKernelSize * _options.PoolingKernelSize, _options.PoolingKernelSize);

        // HF skips the resize when the size already matches; the resampler would be an identity anyway.
        var resized = targetH == height && targetW == width
            ? rgb
            : PilBicubicResizer.ResizeRgb(rgb, width, height, targetW, targetH);

        return ImagePatchifier.Patchify(resized, targetH, targetW, _options.PatchSize, _options.Layout);
    }

    /// <summary>Decodes to straight (non-premultiplied) RGB; alpha is discarded like PIL's convert("RGB").</summary>
    private (byte[] Rgb, int Width, int Height) Decode(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported or corrupt image data.");

        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0 || (long)w * h > _options.MaxSourcePixels)
            throw new InvalidDataException($"Image dimensions {w}x{h} are invalid or exceed the {_options.MaxSourcePixels} pixel limit.");

        // Unpremul so that RGB under partial alpha is untouched (PIL drops alpha without un-premultiplying).
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[checked(w * h * 4)];
        var result = codec.GetPixels(info, rgba);
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Image decode failed: {result}.");

        var rgb = new byte[w * h * 3];
        for (int i = 0, s = 0, d = 0; i < w * h; i++, s += 4, d += 3)
        {
            rgb[d] = rgba[s];
            rgb[d + 1] = rgba[s + 1];
            rgb[d + 2] = rgba[s + 2];
        }

        return _options.ApplyExifOrientation ? ApplyOrientation(rgb, w, h, codec.EncodedOrigin) : (rgb, w, h);
    }

    /// <summary>Equivalent of PIL <c>ImageOps.exif_transpose</c> (values 1-8 = SKEncodedOrigin).</summary>
    internal static (byte[] Rgb, int Width, int Height) ApplyOrientation(byte[] rgb, int w, int h, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return (rgb, w, h);

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? h : w, oh = swap ? w : h;
        var output = new byte[rgb.Length];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int sx, sy;
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight: sx = w - 1 - x; sy = y; break;            // mirror horizontal
                    case SKEncodedOrigin.BottomRight: sx = w - 1 - x; sy = h - 1 - y; break; // rotate 180
                    case SKEncodedOrigin.BottomLeft: sx = x; sy = h - 1 - y; break;         // mirror vertical
                    case SKEncodedOrigin.LeftTop: sx = y; sy = x; break;                     // transpose
                    case SKEncodedOrigin.RightTop: sx = y; sy = h - 1 - x; break;            // rotate 90 CW
                    case SKEncodedOrigin.RightBottom: sx = w - 1 - y; sy = h - 1 - x; break; // transverse
                    case SKEncodedOrigin.LeftBottom: sx = w - 1 - y; sy = x; break;          // rotate 90 CCW
                    default: sx = x; sy = y; break;
                }
                int s = (sy * w + sx) * 3, d = (y * ow + x) * 3;
                output[d] = rgb[s];
                output[d + 1] = rgb[s + 1];
                output[d + 2] = rgb[s + 2];
            }
        }
        return (output, ow, oh);
    }
}
