using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Audio;

public class AudioWindowingTests
{
    private readonly AudioPreprocessor _pre = new();

    private static float[] Tone(double seconds) =>
        AudioTestSignals.Sine(1000, (int)(seconds * AudioTestSignals.SampleRate), 0.3);

    [Fact]
    public void FiveSeconds_IsOneWindowOneClip()
    {
        var windows = _pre.BuildWindows(Tone(5));

        windows.Should().HaveCount(1);
        var w = windows[0];
        w.StartMs.Should().Be(0);
        w.EndMs.Should().Be(5000);
        w.Features.Clips.Should().Be(1);
        w.Features.Frames.Should().Be(499); // (80000 + 160 - 321) / 160 + 1
        w.Features.InputFeatures.Length.Should().Be(499 * 128);
        w.Features.Mask.Should().OnlyContain(m => m);
        AudioTokenMath.CountTokens(w.Features).Should().Be(125); // 5 s / 40 ms
    }

    [Fact]
    public void FortySeconds_IsTwoWindows_FirstSplitIntoThreeClips()
    {
        var windows = _pre.BuildWindows(Tone(40));

        windows.Should().HaveCount(2);
        windows[0].StartMs.Should().Be(0);
        windows[0].EndMs.Should().Be(30_000);
        windows[1].StartMs.Should().Be(30_000);
        windows[1].EndMs.Should().Be(40_000);

        // 30 s = 480000 samples = 179200 + 179200 + 121600 samples.
        var f = windows[0].Features;
        f.Clips.Should().Be(3);
        f.Frames.Should().Be(1119); // (179200 + 160 - 321) / 160 + 1
        f.Mask.AsSpan(0, 1119).ToArray().Should().OnlyContain(m => m);
        f.Mask.AsSpan(1119, 1119).ToArray().Should().OnlyContain(m => m);

        // Third clip: 121600 samples => 760 frames, 759 valid; right-padded with mask=false and zero features.
        var third = f.Mask.AsSpan(2 * 1119, 1119).ToArray();
        third.Count(m => m).Should().Be(759);
        third.Take(759).Should().OnlyContain(m => m);
        third.Skip(759).Should().OnlyContain(m => !m);
        f.InputFeatures.AsSpan((2 * 1119 + 760) * 128, (1119 - 760) * 128).ToArray().Should().OnlyContain(v => v == 0f);

        // Full clips give exactly audio_seq_length tokens.
        AudioTokenMath.CountTokens(f.Mask.AsSpan(0, 1119)).Should().Be(280);

        windows[1].Features.Clips.Should().Be(1);
    }

    [Fact]
    public void SeventyFiveSeconds_IsThreeWindows_LastIsFifteenSeconds()
    {
        var windows = _pre.BuildWindows(Tone(75));

        windows.Select(w => (w.StartMs, w.EndMs)).Should().Equal(
            (0L, 30_000L), (30_000L, 60_000L), (60_000L, 75_000L));
        windows.Select(w => w.Features.Clips).Should().Equal(3, 3, 2);
        foreach (var w in windows)
        {
            w.Features.Mask.Length.Should().Be(w.Features.Clips * w.Features.Frames);
            w.Features.InputFeatures.Length.Should().Be(w.Features.Clips * w.Features.Frames * 128);
        }
    }

    [Fact]
    public void WindowLength_IsConfigurable()
    {
        var pre = new AudioPreprocessor(new AudioPreprocessorOptions { WindowSeconds = 10 });
        var windows = pre.BuildWindows(Tone(25));
        windows.Select(w => w.EndMs - w.StartMs).Should().Equal(10_000L, 10_000L, 5_000L);
        windows[0].Features.Clips.Should().Be(1); // 10 s < 11.2 s clip
    }

    [Fact]
    public void EmptyAndSubFrameAudio_YieldNoWindows()
    {
        _pre.BuildWindows([]).Should().BeEmpty();
        _pre.BuildWindows(new float[100]).Should().BeEmpty();
    }
}
