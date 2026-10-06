namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Order in which 16x16 patches are emitted.</summary>
public enum PatchScanOrder
{
    /// <summary>Patch rows top to bottom, left to right within a row (HF <c>convert_image_to_patches</c>).</summary>
    RowMajor,
    /// <summary>Patch columns left to right, top to bottom within a column.</summary>
    ColumnMajor
}

/// <summary>Order of the 16*16*3 values inside one flattened patch.</summary>
public enum PatchFlattenOrder
{
    /// <summary>[patch row y][patch col x][channel]; HF <c>transpose(1, 3, 2, 4, 0)</c> + reshape.</summary>
    YXC,
    /// <summary>[x][y][channel].</summary>
    XYC,
    /// <summary>[channel][y][x] (planar patch).</summary>
    CYX
}

/// <summary>Order of the two coordinates in <c>pixel_position_ids</c>.</summary>
public enum PositionIdOrder
{
    /// <summary>[column, row]; HF <c>np.stack([grid_x, grid_y])</c>.</summary>
    XY,
    /// <summary>[row, column].</summary>
    YX
}

/// <summary>
/// The three conventions that decide how an image is turned into encoder tensors, kept in one place so golden
/// tests against the real ONNX graph can flip them. Defaults mirror the Hugging Face processor
/// (<c>Gemma4ImageProcessorPil._preprocess</c>, transformers 5.19.0) and are verified against that source;
/// [verify] that the ONNX export of the vision encoder expects exactly the HF processor's tensors.
/// </summary>
public sealed record VisionPatchLayout(PatchScanOrder ScanOrder, PatchFlattenOrder FlattenOrder, PositionIdOrder PositionOrder)
{
    public const PatchScanOrder DefaultScanOrder = PatchScanOrder.RowMajor;
    public const PatchFlattenOrder DefaultFlattenOrder = PatchFlattenOrder.YXC;
    public const PositionIdOrder DefaultPositionOrder = PositionIdOrder.XY;

    public static VisionPatchLayout Default { get; } = new(DefaultScanOrder, DefaultFlattenOrder, DefaultPositionOrder);
}
