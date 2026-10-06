using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

public class PilBicubicResizerTests
{
    public static IEnumerable<object[]> PillowCases()
    {
        var cases = VisionTestSupport.LoadJson("resize_cases.json");
        int i = 0;
        foreach (var c in cases.EnumerateArray())
            yield return [i++, c.GetProperty("srcW").GetInt32(), c.GetProperty("srcH").GetInt32(),
                c.GetProperty("outW").GetInt32(), c.GetProperty("outH").GetInt32(), c.GetProperty("rgbBase64").GetString()!];
    }

    [Theory]
    [MemberData(nameof(PillowCases))]
    public void Resize_matches_pillow_bicubic_exactly(int index, int srcW, int srcH, int outW, int outH, string expectedBase64)
    {
        _ = index;
        var expected = Convert.FromBase64String(expectedBase64);
        var actual = PilBicubicResizer.ResizeRgb(VisionTestSupport.SynthRgb(srcW, srcH), srcW, srcH, outW, outH);

        actual.Should().HaveCount(outW * outH * 3);
        var maxDiff = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
        maxDiff.Should().Be(0, "the port is bit-exact with Pillow's 8bpc fixed-point resampler");
    }

    [Fact]
    public void Resize_same_size_is_a_copy()
    {
        var src = VisionTestSupport.SynthRgb(16, 16);
        var result = PilBicubicResizer.ResizeRgb(src, 16, 16, 16, 16);
        result.Should().Equal(src);
        result.Should().NotBeSameAs(src);
    }

    [Fact]
    public void Resize_constant_image_stays_constant()
    {
        var src = Enumerable.Repeat((byte)200, 31 * 17 * 3).ToArray();
        PilBicubicResizer.ResizeRgb(src, 31, 17, 48, 96).Should().OnlyContain(b => b == 200);
        PilBicubicResizer.ResizeRgb(src, 31, 17, 5, 3).Should().OnlyContain(b => b == 200);
    }

    [Fact]
    public void Resize_validates_arguments()
    {
        var act1 = () => PilBicubicResizer.ResizeRgb(new byte[10], 2, 2, 4, 4);
        act1.Should().Throw<ArgumentException>();
        var act2 = () => PilBicubicResizer.ResizeRgb(new byte[12], 2, 2, 0, 4);
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }
}
