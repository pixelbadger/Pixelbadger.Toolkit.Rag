using System.Text.Json;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Audio;

public class LogMelExtractorTests
{
    private readonly LogMelExtractor _extractor = new();

    [Fact]
    public void Sine_PeaksInTheExpectedMelBin()
    {
        foreach (var hz in new[] { 440.0, 1000.0, 3000.0, 6000.0 })
        {
            var r = _extractor.Extract(AudioTestSignals.Sine(hz, 16_000));
            var mid = r.Features.AsSpan(r.Frames / 2 * 128, 128);
            var expected = AudioTestSignals.ExpectedMelBin(hz);
            AudioTestSignals.ArgMax(mid).Should().BeInRange(expected - 1, expected + 1, $"{hz} Hz");
        }
    }

    [Fact]
    public void Silence_HitsTheLogFloor()
    {
        var r = _extractor.Extract(new float[16_000]);
        var floor = (float)Math.Log(1e-3);
        r.Frames.Should().BeGreaterThan(0);
        for (var f = 0; f < r.Frames; f++)
        {
            if (!r.Mask[f]) continue;
            for (var m = 0; m < 128; m++) r.Features[f * 128 + m].Should().BeApproximately(floor, 1e-6f);
        }
    }

    [Fact]
    public void Shapes_AndMask_FollowHfFraming()
    {
        // 16000 samples (multiple of 128): frames = (16000 + 160 - 321) / 160 + 1 = 99, all valid (f*160 + 160 < 16000).
        var r = _extractor.Extract(new float[16_000]);
        r.Frames.Should().Be(99);
        r.Features.Length.Should().Be(99 * 128);
        r.Mask.Should().OnlyContain(m => m);

        // 300 samples pad to 384 => 2 frames, only the first valid.
        _extractor.Extract(new float[300]).Mask.Should().Equal(true, false);
        _extractor.Extract(new float[0]).Frames.Should().Be(0);
    }

    [Fact]
    public void PaddedFrames_AreZeroed()
    {
        var r = _extractor.Extract(AudioTestSignals.Sine(1000, 300));
        r.Frames.Should().Be(2);
        r.Mask[1].Should().BeFalse();
        r.Features.AsSpan(128, 128).ToArray().Should().OnlyContain(v => v == 0f);
    }

    public static TheoryData<string> Cases => new() { "tones", "chirp", "short" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesHuggingFaceReference(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-assets", "audio", "logmel_reference.json")));
        var c = doc.RootElement.GetProperty("cases").GetProperty(name);
        var n = c.GetProperty("samples").GetInt32();
        var expectedMask = c.GetProperty("mask").GetString()!.Select(ch => ch == '1').ToArray();
        var bytes = Convert.FromBase64String(c.GetProperty("features_f32_b64").GetString()!);
        var expected = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, expected, 0, bytes.Length);

        var signal = name switch { "tones" => AudioTestSignals.Tones(n), "chirp" => AudioTestSignals.Chirp(n), _ => AudioTestSignals.Short(n) };
        var r = _extractor.Extract(signal);

        r.Frames.Should().Be(c.GetProperty("frames").GetInt32());
        r.Mask.Should().Equal(expectedMask);
        r.Features.Should().HaveCount(expected.Length);
        var maxDiff = 0f;
        for (var i = 0; i < expected.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(expected[i] - r.Features[i]));
        maxDiff.Should().BeLessThan(1e-4f);
    }
}
