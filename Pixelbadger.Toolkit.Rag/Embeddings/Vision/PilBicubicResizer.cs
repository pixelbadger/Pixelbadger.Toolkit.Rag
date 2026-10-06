namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>
/// Pure C# port of Pillow's <c>Image.resize(size, resample=Image.BICUBIC)</c> for 8-bit interleaved RGB
/// (Pillow src/libImaging/Resample.c: <c>ImagingResampleHorizontal_8bpc</c> / <c>Vertical_8bpc</c>,
/// <c>precompute_coeffs</c>, <c>normalize_coeffs_8bpc</c>). Separable: horizontal pass first, then vertical;
/// a pass is skipped when that dimension is unchanged. Bicubic kernel a = -0.5, support scaled by the
/// downscale factor (antialiasing), 22-bit fixed-point coefficients, intermediate and final values rounded and
/// clamped to uint8. Pillow's <c>reducing_gap</c> is not used by the HF resize call and is not implemented.
/// </summary>
public static class PilBicubicResizer
{
    private const int PrecisionBits = 32 - 8 - 2;
    private const double CubicA = -0.5;
    private const double Support = 2.0;

    /// <summary>Resizes interleaved RGB (3 bytes/pixel). Always returns a new array, even if dimensions are unchanged.</summary>
    public static byte[] ResizeRgb(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "Dimensions must be positive.");
        if (src.Length != checked(srcWidth * srcHeight * 3))
            throw new ArgumentException("Source length does not match dimensions.", nameof(src));

        var current = src.ToArray();
        int curWidth = srcWidth;

        if (dstWidth != srcWidth)
        {
            var (bounds, coeffs, ksize) = PrecomputeCoeffs(srcWidth, dstWidth);
            var tmp = new byte[dstWidth * srcHeight * 3];
            for (int y = 0; y < srcHeight; y++)
            {
                int srcRow = y * curWidth * 3;
                int dstRow = y * dstWidth * 3;
                for (int x = 0; x < dstWidth; x++)
                {
                    int xmin = bounds[x * 2], n = bounds[x * 2 + 1], kOff = x * ksize;
                    for (int c = 0; c < 3; c++)
                    {
                        long acc = 1L << (PrecisionBits - 1);
                        for (int i = 0; i < n; i++)
                            acc += current[srcRow + (xmin + i) * 3 + c] * (long)coeffs[kOff + i];
                        tmp[dstRow + x * 3 + c] = Clip8(acc);
                    }
                }
            }
            current = tmp;
            curWidth = dstWidth;
        }

        if (dstHeight != srcHeight)
        {
            var (bounds, coeffs, ksize) = PrecomputeCoeffs(srcHeight, dstHeight);
            var tmp = new byte[curWidth * dstHeight * 3];
            int stride = curWidth * 3;
            for (int y = 0; y < dstHeight; y++)
            {
                int ymin = bounds[y * 2], n = bounds[y * 2 + 1], kOff = y * ksize;
                for (int xc = 0; xc < stride; xc++)
                {
                    long acc = 1L << (PrecisionBits - 1);
                    for (int i = 0; i < n; i++)
                        acc += current[(ymin + i) * stride + xc] * (long)coeffs[kOff + i];
                    tmp[y * stride + xc] = Clip8(acc);
                }
            }
            current = tmp;
        }

        return current;
    }

    private static byte Clip8(long acc)
    {
        long v = acc >> PrecisionBits; // arithmetic shift, like Pillow's clip8_lookups index
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    private static double BicubicFilter(double x)
    {
        if (x < 0.0) x = -x;
        if (x < 1.0) return ((CubicA + 2.0) * x - (CubicA + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * CubicA;
        return 0.0;
    }

    /// <summary>Pillow <c>precompute_coeffs</c> (box = whole image) followed by <c>normalize_coeffs_8bpc</c>.</summary>
    private static (int[] Bounds, int[] Coeffs, int KSize) PrecomputeCoeffs(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterScale = scale < 1.0 ? 1.0 : scale;
        double support = Support * filterScale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var coeffs = new int[outSize * ksize];
        var pre = new double[ksize];
        double ss = 1.0 / filterScale;

        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;

            Array.Clear(pre);
            double ww = 0.0;
            for (int x = 0; x < xmax; x++)
            {
                double w = BicubicFilter((x + xmin - center + 0.5) * ss);
                pre[x] = w;
                ww += w;
            }
            if (ww != 0.0)
                for (int x = 0; x < xmax; x++) pre[x] /= ww;

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
            for (int x = 0; x < ksize; x++)
            {
                double p = pre[x] * (1 << PrecisionBits);
                coeffs[xx * ksize + x] = (int)(pre[x] < 0 ? -0.5 + p : 0.5 + p);
            }
        }

        return (bounds, coeffs, ksize);
    }
}
