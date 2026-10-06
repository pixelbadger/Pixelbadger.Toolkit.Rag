using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using SkiaSharp;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

public class ImagePreprocessorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pbrag-vision-" + Guid.NewGuid().ToString("N"));

    public ImagePreprocessorTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, true);

    public static IEnumerable<object[]> E2ECases()
    {
        foreach (var c in VisionTestSupport.LoadJson("e2e_cases.json").EnumerateArray())
            yield return [c.GetProperty("file").GetString()!, c.GetProperty("maxSoftTokens").GetInt32(), c.GetProperty("height").GetInt32(),
                c.GetProperty("width").GetInt32(), c.GetProperty("numPatches").GetInt32(), c.GetProperty("rgbSha256").GetString()!,
                c.GetProperty("pixelValuesSha256").GetString()!, c.GetProperty("positionIdsSha256").GetString()!];
    }

    [Theory]
    [MemberData(nameof(E2ECases))]
    public async Task Pipeline_matches_hf_processor_reference(string file, int budget, int h, int w, int patches, string rgbSha, string pixelsSha, string posSha)
    {
        _ = rgbSha;
        var pre = new ImagePreprocessor(new ImagePreprocessorOptions { MaxSoftTokens = budget });
        var img = await pre.PreprocessAsync(VisionTestSupport.Asset("images", file));

        img.Height.Should().Be(h);
        img.Width.Should().Be(w);
        img.NumPatches.Should().Be(patches);
        img.PixelValues.Should().HaveCount(patches * 768);
        img.PositionIds.Should().HaveCount(patches * 2);
        img.ExpectedSoftTokens.Should().BeLessThanOrEqualTo(budget);

        // Hashes are over numpy float32 / int64 little-endian bytes (exact equality incl. the resize).
        VisionTestSupport.Sha256Hex(System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.PixelValues.AsSpan())).Should().Be(pixelsSha);
        VisionTestSupport.Sha256Hex(System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.PositionIds.AsSpan())).Should().Be(posSha);
    }

    [Fact]
    public async Task Alpha_is_dropped_not_composited()
    {
        // The rgba fixture and its RGB twin produce identical tensors (PIL convert("RGB") ignores alpha).
        var pre = new ImagePreprocessor();
        var rgba = await pre.PreprocessAsync(VisionTestSupport.Asset("images", "rgba_100x70.png"));
        var rgb = await pre.PreprocessAsync(VisionTestSupport.Asset("images", "rgb_100x70.png"));
        rgba.PixelValues.Should().Equal(rgb.PixelValues);
    }

    [Fact]
    public async Task Grayscale_is_replicated_across_channels()
    {
        var pre = new ImagePreprocessor(new ImagePreprocessorOptions { MaxSoftTokens = 70 });
        var img = await pre.PreprocessAsync(VisionTestSupport.Asset("images", "gray_100x70.png"));
        for (int i = 0; i < img.PixelValues.Length; i += 3)
        {
            img.PixelValues[i + 1].Should().Be(img.PixelValues[i]);
            img.PixelValues[i + 2].Should().Be(img.PixelValues[i]);
        }
    }

    [Fact]
    public async Task Values_are_in_unit_range()
    {
        var img = await new ImagePreprocessor().PreprocessAsync(VisionTestSupport.Asset("images", "rgb_100x70.png"));
        img.PixelValues.Min().Should().BeGreaterThanOrEqualTo(0f);
        img.PixelValues.Max().Should().BeLessThanOrEqualTo(1f);
        img.PixelValues.Max().Should().BeGreaterThan(0.9f);
    }

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        // Stored 96x48 (W x H) with quadrants red/green (top) and blue/yellow (bottom); orientation 6 = rotate 90 CW.
        // Displayed: 48 wide x 96 high; top-left quadrant is the stored bottom-left (blue).
        var path = VisionTestSupport.Asset("images", "exif_rot6.jpg");
        var rotated = await new ImagePreprocessor().PreprocessAsync(path);
        rotated.Height.Should().BeGreaterThan(rotated.Width);

        var raw = await new ImagePreprocessor(new ImagePreprocessorOptions { ApplyExifOrientation = false }).PreprocessAsync(path);
        raw.Width.Should().BeGreaterThan(raw.Height);

        // First patch, first pixel of the oriented image should be blue-ish (JPEG tolerance).
        rotated.PixelValues[0].Should().BeLessThan(0.2f);
        rotated.PixelValues[1].Should().BeLessThan(0.2f);
        rotated.PixelValues[2].Should().BeGreaterThan(0.8f);
        // Unrotated: first pixel is red.
        raw.PixelValues[0].Should().BeGreaterThan(0.8f);
        raw.PixelValues[2].Should().BeLessThan(0.2f);
    }

    [Fact]
    public async Task Jpeg_and_webp_round_trips_decode()
    {
        using var bmp = new SKBitmap(64, 48);
        bmp.Erase(SKColors.Teal);
        var pre = new ImagePreprocessor(new ImagePreprocessorOptions { MaxSoftTokens = 70 });
        foreach (var (fmt, name) in new[] { (SKEncodedImageFormat.Jpeg, "a.jpg"), (SKEncodedImageFormat.Webp, "a.webp"), (SKEncodedImageFormat.Png, "a.png") })
        {
            var path = Path.Combine(_tempDir, name);
            using (var data = SKImage.FromBitmap(bmp).Encode(fmt, 100))
            await File.WriteAllBytesAsync(path, data.ToArray());
            var img = await pre.PreprocessAsync(path);
            (img.Height % 48).Should().Be(0);
            (img.Width % 48).Should().Be(0);
            img.PixelValues[0].Should().BeApproximately(0f, 0.03f);
            img.PixelValues[1].Should().BeApproximately(128f / 255f, 0.03f);
        }
    }

    [Fact]
    public async Task Cancellation_and_errors()
    {
        var pre = new ImagePreprocessor();
        var missing = () => pre.PreprocessAsync(Path.Combine(_tempDir, "nope.png"));
        await missing.Should().ThrowAsync<FileNotFoundException>();

        var junk = Path.Combine(_tempDir, "junk.png");
        await File.WriteAllBytesAsync(junk, [1, 2, 3, 4, 5]);
        var bad = () => pre.PreprocessAsync(junk);
        await bad.Should().ThrowAsync<InvalidDataException>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = () => pre.PreprocessAsync(VisionTestSupport.Asset("images", "tiny_7x5.png"), cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Options_are_validated()
    {
        var act = () => new ImagePreprocessor(new ImagePreprocessorOptions { MaxSoftTokens = 100 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Square_image_gets_768x768_and_256_soft_tokens_by_default()
    {
        var img = new ImagePreprocessor().PreprocessRgb(VisionTestSupport.SynthRgb(100, 100), 100, 100);
        (img.Height, img.Width).Should().Be((768, 768));
        img.NumPatches.Should().Be(2304);
        img.ExpectedSoftTokens.Should().Be(256);
    }

    [Fact]
    public void Already_sized_image_is_not_resampled()
    {
        // 384x384 with budget 70 is already the target size (24x24 patches = 64 soft tokens).
        var rgb = VisionTestSupport.SynthRgb(384, 384);
        var img = new ImagePreprocessor(new ImagePreprocessorOptions { MaxSoftTokens = 70 }).PreprocessRgb(rgb, 384, 384);
        (img.Height, img.Width).Should().Be((384, 384));
        img.PixelValues[0].Should().Be((float)(rgb[0] * (1.0 / 255.0)));
    }

    [Fact]
    public void Di_resolves_parameterless_constructor()
    {
        using var sp = new ServiceCollection().AddTransient<IImagePreprocessor, ImagePreprocessor>().BuildServiceProvider();
        sp.GetRequiredService<IImagePreprocessor>().Should().BeOfType<ImagePreprocessor>();
    }
}
