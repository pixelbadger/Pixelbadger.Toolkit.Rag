namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Rescales RGB bytes to [0,1] and cuts them into flattened patches plus grid position ids.</summary>
public static class ImagePatchifier
{
    public const int Channels = 3;

    private static readonly float[] RescaleTable = BuildRescaleTable();

    // HF: rescale does astype(float64) * (1/255) and casts back to float32.
    private static float[] BuildRescaleTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = (float)(i * (1.0 / 255.0));
        return t;
    }

    /// <summary>
    /// Mirrors <c>convert_image_to_patches</c> + the position-id meshgrid of
    /// <c>Gemma4ImageProcessorPil._preprocess</c>. <paramref name="rgb"/> is interleaved HWC; height and width must
    /// be multiples of <paramref name="patchSize"/>. Patches are not padded to the budget (batch of one image).
    /// </summary>
    public static PreprocessedImage Patchify(byte[] rgb, int height, int width, int patchSize, VisionPatchLayout layout)
    {
        if (height % patchSize != 0 || width % patchSize != 0)
            throw new ArgumentException($"Image {width}x{height} is not divisible by patch size {patchSize}.");
        if (rgb.Length != height * width * Channels)
            throw new ArgumentException("RGB buffer length does not match dimensions.", nameof(rgb));

        int gridH = height / patchSize, gridW = width / patchSize;
        int numPatches = gridH * gridW;
        int patchValues = patchSize * patchSize * Channels;

        // Offset (relative to the patch's top-left pixel in the HWC buffer) of each flattened element.
        var offsets = new int[patchValues];
        int e = 0;
        switch (layout.FlattenOrder)
        {
            case PatchFlattenOrder.YXC:
                for (int y = 0; y < patchSize; y++) for (int x = 0; x < patchSize; x++) for (int c = 0; c < Channels; c++)
                    offsets[e++] = (y * width + x) * Channels + c;
                break;
            case PatchFlattenOrder.XYC:
                for (int x = 0; x < patchSize; x++) for (int y = 0; y < patchSize; y++) for (int c = 0; c < Channels; c++)
                    offsets[e++] = (y * width + x) * Channels + c;
                break;
            case PatchFlattenOrder.CYX:
                for (int c = 0; c < Channels; c++) for (int y = 0; y < patchSize; y++) for (int x = 0; x < patchSize; x++)
                    offsets[e++] = (y * width + x) * Channels + c;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(layout));
        }

        var pixels = new float[numPatches * patchValues];
        var positions = new long[numPatches * 2];
        for (int p = 0; p < numPatches; p++)
        {
            int ph, pw;
            if (layout.ScanOrder == PatchScanOrder.RowMajor) { ph = p / gridW; pw = p % gridW; }
            else { pw = p / gridH; ph = p % gridH; }

            int origin = (ph * patchSize * width + pw * patchSize) * Channels;
            int dst = p * patchValues;
            for (int i = 0; i < patchValues; i++)
                pixels[dst + i] = RescaleTable[rgb[origin + offsets[i]]];

            if (layout.PositionOrder == PositionIdOrder.XY) { positions[p * 2] = pw; positions[p * 2 + 1] = ph; }
            else { positions[p * 2] = ph; positions[p * 2 + 1] = pw; }
        }

        return new PreprocessedImage(pixels, positions, numPatches, height, width);
    }
}
