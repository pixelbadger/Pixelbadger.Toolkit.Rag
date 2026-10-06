using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

public class ImagePatchifierTests
{
    // 32x48 image (H x W): 2 patch rows x 3 patch columns, each pixel's channels hold (x, y, c-tag) so positions are identifiable.
    private static byte[] Marked(int h, int w)
    {
        var a = new byte[h * w * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                a[i] = (byte)x; a[i + 1] = (byte)y; a[i + 2] = 255;
            }
        return a;
    }

    private static float Px(byte v) => (float)(v * (1.0 / 255.0));

    [Fact]
    public void Shapes_and_default_layout_match_hf()
    {
        var img = ImagePatchifier.Patchify(Marked(32, 48), 32, 48, 16, VisionPatchLayout.Default);

        img.NumPatches.Should().Be(6);
        img.PixelValues.Should().HaveCount(6 * 768);
        img.PositionIds.Should().Equal(0, 0, 1, 0, 2, 0, 0, 1, 1, 1, 2, 1); // (x, y), row-major
        // patch 1 (row 0, col 1), element 0 = pixel (y0,x16) channel 0; element 1 = channel 1; element 3 = pixel x17.
        img.PixelValues[768 + 0].Should().Be(Px(16));
        img.PixelValues[768 + 1].Should().Be(Px(0));
        img.PixelValues[768 + 2].Should().Be(Px(255));
        img.PixelValues[768 + 3].Should().Be(Px(17));
        // next row of the patch starts after 16 pixels * 3 channels: y = 1
        img.PixelValues[768 + 48 + 1].Should().Be(Px(1));
    }

    [Fact]
    public void Rescale_is_exactly_float32_of_v_times_double_one_over_255()
    {
        var rgb = new byte[16 * 16 * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i % 256);
        var img = ImagePatchifier.Patchify(rgb, 16, 16, 16, VisionPatchLayout.Default);
        for (int i = 0; i < 768; i++)
            img.PixelValues[i].Should().Be((float)((i % 256) * (1.0 / 255.0)));
        img.PixelValues.Max().Should().BeLessThanOrEqualTo(1f);
    }

    [Fact]
    public void Flipping_position_order_swaps_columns()
    {
        var layout = VisionPatchLayout.Default with { PositionOrder = PositionIdOrder.YX };
        var img = ImagePatchifier.Patchify(Marked(32, 48), 32, 48, 16, layout);
        img.PositionIds.Should().Equal(0, 0, 0, 1, 0, 2, 1, 0, 1, 1, 1, 2);
    }

    [Fact]
    public void Column_major_scan_reorders_patches()
    {
        var layout = VisionPatchLayout.Default with { ScanOrder = PatchScanOrder.ColumnMajor };
        var img = ImagePatchifier.Patchify(Marked(32, 48), 32, 48, 16, layout);
        img.PositionIds.Should().Equal(0, 0, 0, 1, 1, 0, 1, 1, 2, 0, 2, 1);
        img.PixelValues[768].Should().Be(Px(0));  // second patch is (row 1, col 0): x = 0
        img.PixelValues[768 + 1].Should().Be(Px(16)); // y = 16
    }

    [Fact]
    public void Flatten_orders_differ_as_documented()
    {
        var rgb = Marked(16, 16);
        var yxc = ImagePatchifier.Patchify(rgb, 16, 16, 16, VisionPatchLayout.Default with { FlattenOrder = PatchFlattenOrder.YXC });
        var xyc = ImagePatchifier.Patchify(rgb, 16, 16, 16, VisionPatchLayout.Default with { FlattenOrder = PatchFlattenOrder.XYC });
        var cyx = ImagePatchifier.Patchify(rgb, 16, 16, 16, VisionPatchLayout.Default with { FlattenOrder = PatchFlattenOrder.CYX });

        // element 3 = second pixel: YXC -> x=1; XYC -> y=1 (x fixed 0, channel 0 is x => 0)
        yxc.PixelValues[3].Should().Be(Px(1));      // channel 0 (x) of pixel (y0,x1)
        xyc.PixelValues[3].Should().Be(Px(0));      // channel 0 (x) of pixel (y1,x0)
        xyc.PixelValues[4].Should().Be(Px(1));      // channel 1 (y) of pixel (y1,x0)
        cyx.PixelValues[1].Should().Be(Px(1));      // plane 0 (x), second pixel
        cyx.PixelValues[256 + 16].Should().Be(Px(1)); // plane 1 (y), second row
        // all three are permutations of the same values
        xyc.PixelValues.OrderBy(v => v).Should().Equal(yxc.PixelValues.OrderBy(v => v));
        cyx.PixelValues.OrderBy(v => v).Should().Equal(yxc.PixelValues.OrderBy(v => v));
    }

    [Fact]
    public void Rejects_dimensions_not_divisible_by_patch_size()
    {
        var act = () => ImagePatchifier.Patchify(new byte[17 * 16 * 3], 17, 16, 16, VisionPatchLayout.Default);
        act.Should().Throw<ArgumentException>();
    }
}
