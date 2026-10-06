using System.Diagnostics;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Audio;

public sealed class AudioPreprocessorFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pbrag-audio-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static bool FfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateWav(string name, double hz, double seconds, int sampleRate = 44_100)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={hz}:duration={seconds}:sample_rate={sampleRate}", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0);
        return path;
    }

    [SkippableFact]
    public async Task DecodesGeneratedWav_ResamplesTo16kAndFindsThePeak()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg not installed");
        var wav = GenerateWav("tone.wav", 1000, 2.0, sampleRate: 44_100); // resampled to 16 kHz by ffmpeg

        var windows = await new AudioPreprocessor().PreprocessAsync(wav);

        windows.Should().HaveCount(1);
        var w = windows[0];
        w.StartMs.Should().Be(0);
        w.EndMs.Should().BeInRange(1990, 2010);
        w.Features.Clips.Should().Be(1);
        w.Features.Frames.Should().BeInRange(195, 205);
        var mid = w.Features.InputFeatures.AsSpan(w.Features.Frames / 2 * 128, 128);
        var expected = AudioTestSignals.ExpectedMelBin(1000);
        AudioTestSignals.ArgMax(mid).Should().BeInRange(expected - 1, expected + 1);
    }

    [SkippableFact]
    public async Task LongerThanWindow_ProducesMultipleWindows()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg not installed");
        var wav = GenerateWav("long.wav", 440, 5.0, sampleRate: 16_000);

        var windows = await new AudioPreprocessor(new AudioPreprocessorOptions { WindowSeconds = 2 }).PreprocessAsync(wav);

        windows.Select(w => w.StartMs).Should().Equal(0L, 2000L, 4000L);
    }

    [SkippableFact]
    public async Task CorruptFile_GivesClearError()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg not installed");
        var path = Path.Combine(_dir, "bad.wav");
        await File.WriteAllTextAsync(path, "this is not audio");

        var act = () => new AudioPreprocessor().PreprocessAsync(path);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ffmpeg failed to decode*");
    }

    [SkippableFact]
    public async Task CancelledToken_Cancels()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg not installed");
        var wav = GenerateWav("c.wav", 440, 1.0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new AudioPreprocessor().PreprocessAsync(wav, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MissingFfmpegBinary_GivesClearError()
    {
        var path = Path.Combine(_dir, "x.wav");
        await File.WriteAllBytesAsync(path, [0]);
        var pre = new AudioPreprocessor(new AudioPreprocessorOptions { FfmpegPath = Path.Combine(_dir, "no-such-ffmpeg") });

        var act = () => pre.PreprocessAsync(path);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Could not start ffmpeg*");
    }

    [Fact]
    public async Task MissingInputFile_ThrowsFileNotFound()
    {
        var act = () => new AudioPreprocessor().PreprocessAsync(Path.Combine(_dir, "nope.wav"));
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
