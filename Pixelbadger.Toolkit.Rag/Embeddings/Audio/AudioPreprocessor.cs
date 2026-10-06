using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>Settings for <see cref="AudioPreprocessor"/>.</summary>
public sealed record AudioPreprocessorOptions
{
    public const int SampleRate = 16_000;

    /// <summary>ffmpeg executable name or full path.</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>
    /// Length of one <see cref="AudioWindow"/> (= one chunk). ~30 s: HF's extractor defaults to
    /// <c>max_length=480000</c> samples (30 s) [verified in Gemma4AudioFeatureExtractor], and
    /// ~30 s windows are the recommended size.
    /// </summary>
    public double WindowSeconds { get; init; } = 30.0;

    /// <summary>
    /// Soft tokens per clip (<c>audio_seq_length</c>, 280 in the EmbeddingGemma 2 config). At 40 ms per token a
    /// clip is 11.2 s = 179,200 samples = 1,119 mel frames, which the encoder's two stride-2 convs reduce to exactly 280 tokens.
    /// </summary>
    public int ClipTokens { get; init; } = 280;

    /// <summary>Milliseconds of audio per soft token (processor_config <c>audio_ms_per_token</c>).</summary>
    public int MsPerToken { get; init; } = 40;

    /// <summary>Safety cap on decoded duration; ffmpeg is killed beyond it.</summary>
    public double MaxDurationSeconds { get; init; } = 4 * 3600;

    public LogMelOptions LogMel { get; init; } = LogMelOptions.Default;

    internal int WindowSamples => Math.Max(1, (int)Math.Round(WindowSeconds * SampleRate));
    internal int ClipSamples => Math.Max(1, ClipTokens * MsPerToken * SampleRate / 1000);
}

/// <summary>
/// Decodes audio with ffmpeg (16 kHz mono f32le over stdout), splits it into ~30 s windows and turns each window into
/// encoder inputs: log-mel features cut into clips of <see cref="AudioPreprocessorOptions.ClipTokens"/> tokens along the
/// clips axis (right-padded with mask=false).
/// </summary>
public sealed class AudioPreprocessor : IAudioPreprocessor
{
    private readonly AudioPreprocessorOptions _options;
    private readonly LogMelExtractor _logMel;

    public AudioPreprocessor() : this(new AudioPreprocessorOptions()) { }

    public AudioPreprocessor(AudioPreprocessorOptions options)
    {
        _options = options;
        _logMel = new LogMelExtractor(options.LogMel);
    }

    public async Task<IReadOnlyList<AudioWindow>> PreprocessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        var samples = await DecodeAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => BuildWindows(samples, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure function: 16 kHz mono samples to windows. Window k covers samples [k*W, (k+1)*W) with
    /// StartMs/EndMs derived from the sample offsets. Within a window the signal is cut into clips of
    /// <see cref="AudioPreprocessorOptions.ClipSamples"/> samples; each clip gets its own log-mel (as HF does for
    /// separate audio inputs: independent semicausal padding), and clips are right-padded to the longest clip's frame count
    /// with zeros and mask=false. [verify] against golden tensors: HF may instead compute mel over the whole window and cut frames.
    /// Windows/clips that yield zero frames (shorter than ~20 ms) are skipped; empty audio yields no windows.
    /// </summary>
    public IReadOnlyList<AudioWindow> BuildWindows(float[] samples, CancellationToken cancellationToken = default)
    {
        var windows = new List<AudioWindow>();
        var windowSamples = _options.WindowSamples;
        var clipSamples = _options.ClipSamples;

        for (long start = 0; start < samples.Length; start += windowSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(windowSamples, samples.Length - start);
            var span = samples.AsSpan((int)start, length);

            var clips = new List<LogMelResult>();
            for (var c = 0; c < length; c += clipSamples)
            {
                var r = _logMel.Extract(span.Slice(c, Math.Min(clipSamples, length - c)));
                if (r.Frames > 0 && r.Mask.Any(m => m)) clips.Add(r);
            }
            if (clips.Count == 0) continue;

            var frames = clips.Max(c => c.Frames);
            var mel = PreprocessedAudio.MelBins;
            var features = new float[clips.Count * frames * mel];
            var mask = new bool[clips.Count * frames];
            for (var i = 0; i < clips.Count; i++)
            {
                Array.Copy(clips[i].Features, 0, features, (long)i * frames * mel, clips[i].Frames * mel);
                Array.Copy(clips[i].Mask, 0, mask, i * frames, clips[i].Frames);
            }

            var startMs = start * 1000 / AudioPreprocessorOptions.SampleRate;
            var endMs = (start + length) * 1000 / AudioPreprocessorOptions.SampleRate;
            windows.Add(new AudioWindow(startMs, endMs, new PreprocessedAudio(features, mask, clips.Count, frames)));
        }

        return windows;
    }

    private async Task<float[]> DecodeAsync(string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", filePath, "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Could not start ffmpeg ('{_options.FfmpegPath}'). Audio ingestion requires ffmpeg on the PATH (or a configured path): {ex.Message}", ex);
        }

        using var registration = ct.Register(() => TryKill(process));
        var maxBytes = (long)(_options.MaxDurationSeconds * AudioPreprocessorOptions.SampleRate) * sizeof(float);

        // Read stderr concurrently so a full pipe never blocks ffmpeg.
        var stderrTask = ReadBoundedAsync(process.StandardError);
        var pcm = new MemoryStream();
        var tooLong = false;
        try
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                pcm.Write(buffer, 0, read);
                if (pcm.Length > maxBytes) { tooLong = true; TryKill(process); break; }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (tooLong)
            throw new InvalidOperationException($"Audio longer than the configured limit of {_options.MaxDurationSeconds:0} s: {filePath}");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed to decode '{filePath}' (exit code {process.ExitCode}): {stderr.Trim()}");

        var bytes = pcm.GetBuffer().AsSpan(0, (int)(pcm.Length / sizeof(float) * sizeof(float)));
        var samples = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(samples); // f32le; little-endian hosts only
        return samples;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 8192;
        var sb = new StringBuilder();
        var buf = new char[1024];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            if (sb.Length < limit) sb.Append(buf, 0, Math.Min(n, limit - sb.Length));
        return sb.ToString();
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
