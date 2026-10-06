using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

public class AspectRatioSizingTests
{
    private static (int H, int W) Size(int h, int w, int budget = 280) =>
        AspectRatioSizing.GetAspectRatioPreservingSize(h, w, 16, budget * 9, 3);

    [Theory]
    [InlineData(70, 100, 336, 480, 70)]    // values cross-checked with the HF function in generate_fixtures.py
    [InlineData(70, 100, 672, 960, 280)]
    [InlineData(40, 300, 288, 2160, 280)]
    [InlineData(5, 7, 672, 912, 280)]
    public void Matches_hf_rule(int srcH, int srcW, int expH, int expW, int budget)
    {
        var (h, w) = Size(srcH, srcW, budget);
        (h, w).Should().Be((expH, expW));
    }

    [Fact]
    public void Square_images_fill_the_budget_with_divisible_dims()
    {
        // sqrt(2520*256)=803 -> floor(803/48)=16 -> 768x768 -> 256 soft tokens
        Size(1000, 1000).Should().Be((768, 768));
        (768 / 48 * (768 / 48)).Should().Be(256);
    }

    [Theory]
    [InlineData(70)]
    [InlineData(140)]
    [InlineData(280)]
    [InlineData(560)]
    [InlineData(1120)]
    public void Dims_are_divisible_by_48_and_within_budget(int budget)
    {
        foreach (var (h, w) in new[] { (1, 1000), (3000, 4000), (480, 640), (1080, 1920), (500, 100), (48, 48), (4000, 30) })
        {
            int th, tw;
            try { (th, tw) = Size(h, w, budget); }
            catch (InvalidDataException) { continue; }
            (th % 48).Should().Be(0);
            (tw % 48).Should().Be(0);
            (th / 48 * (tw / 48)).Should().BeLessThanOrEqualTo(budget);
        }
    }

    [Fact]
    public void Extreme_aspect_ratio_clamps_short_side_to_one_cell()
    {
        // 10 x 2000 with budget 70 rounds the height down to 0 -> 48 high, width = min(floor(200)*48, 70*48)
        var (h, w) = Size(10, 2000, 70);
        h.Should().Be(48);
        w.Should().Be(70 * 48);
    }

    [Fact]
    public void Rejects_non_positive_dimensions()
    {
        var act = () => Size(0, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
