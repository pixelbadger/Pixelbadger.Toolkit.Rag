using System.Numerics;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>Mel scale used to place the filterbank centres.</summary>
public enum MelScale
{
    /// <summary>2595 * log10(1 + f / 700). The HF Gemma 4 / EmbeddingGemma 2 default [verified against transformers source].</summary>
    Htk,

    /// <summary>Slaney / librosa scale (linear below 1 kHz, logarithmic above).</summary>
    Slaney
}

/// <summary>
/// Log-mel front-end parameters. Defaults reproduce the Hugging Face
/// <c>Gemma4AudioFeatureExtractor</c> (transformers/models/gemma4/feature_extraction_gemma4.py), which
/// EmbeddingGemma 2 reuses: 16 kHz, 20 ms frame (320), 10 ms hop (160), FFT 512, 128 mel bins 0-8000 Hz,
/// periodic Hann, HTK mel, no filterbank normalisation, magnitude (not power) spectrum, log(mel + 0.001).
/// </summary>
public sealed record LogMelOptions
{
    public int SampleRate { get; init; } = 16_000;
    public int FrameLength { get; init; } = 320;
    public int HopLength { get; init; } = 160;
    public int FftLength { get; init; } = 512;
    public int MelBins { get; init; } = PreprocessedAudio.MelBins;
    public double MinFrequency { get; init; } = 0.0;
    public double MaxFrequency { get; init; } = 8000.0;

    /// <summary>Added to the mel energy before the natural log (HF: <c>np.log(mel_spec + mel_floor)</c>, additive, not a max()).</summary>
    public double MelFloor { get; init; } = 1e-3;

    /// <summary>Periodic (true, HF default; equals torch.hann_window) or symmetric Hann.</summary>
    public bool PeriodicWindow { get; init; } = true;

    public MelScale MelScale { get; init; } = MelScale.Htk;

    /// <summary>Slaney area normalisation of the filters. HF Gemma 4 uses <c>norm=None</c>.</summary>
    public bool SlaneyNormalization { get; init; } = false;

    /// <summary>
    /// HF's feature extractor right-pads each input to a multiple of this many samples
    /// (<c>pad_to_multiple_of=128</c>); padded frames are masked out and zeroed.
    /// </summary>
    public int PadToMultipleOf { get; init; } = 128;

    public static LogMelOptions Default { get; } = new();
}

/// <summary>Result of <see cref="LogMelExtractor.Extract"/>: row-major [Frames, MelBins] features and per-frame validity.</summary>
public sealed record LogMelResult(float[] Features, bool[] Mask, int Frames);

/// <summary>
/// Pure-C# port of the Hugging Face <c>Gemma4AudioFeatureExtractor._extract_spectrogram</c>
/// (transformers/models/gemma4/feature_extraction_gemma4.py) with its own radix-2 FFT and mel filterbank
/// (ported from <c>transformers.audio_utils.mel_filter_bank</c> / <c>window_function</c>).
/// Thread-safe after construction.
/// </summary>
public sealed class LogMelExtractor
{
    private readonly LogMelOptions _o;
    private readonly float[] _window; // float32 like HF (window.astype(np.float32)); the float32 product is deterministic and matters at 1e-4
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _bitReverse;
    private readonly int _bins;

    // Sparse filterbank: for each mel filter, the first FFT bin and its weights.
    private readonly int[] _filterStart;
    private readonly double[][] _filterWeights;

    public LogMelExtractor() : this(LogMelOptions.Default) { }

    public LogMelExtractor(LogMelOptions options)
    {
        _o = options;
        if (!BitOperations.IsPow2(options.FftLength) || options.FftLength < options.FrameLength)
            throw new ArgumentException("FftLength must be a power of two >= FrameLength.", nameof(options));
        _bins = options.FftLength / 2 + 1;
        _window = CreateHann(options.FrameLength, options.PeriodicWindow).Select(v => (float)v).ToArray();
        (_cos, _sin, _bitReverse) = CreateFftTables(options.FftLength);
        (_filterStart, _filterWeights) = Sparsify(CreateMelFilterBank(options));
    }

    public LogMelOptions Options => _o;

    /// <summary>
    /// Number of mel frames HF produces for <paramref name="paddedSampleCount"/> samples (after right padding):
    /// <c>(N + frame/2 - (frame+1)) // hop + 1</c> (semicausal left pad of frame/2, unfold size frame+1).
    /// </summary>
    public int FrameCount(int paddedSampleCount)
    {
        var n = paddedSampleCount + _o.FrameLength / 2 - (_o.FrameLength + 1);
        return n < 0 ? 0 : n / _o.HopLength + 1;
    }

    /// <summary>
    /// Computes log-mel features for one waveform (16 kHz mono, float in [-1, 1]), mirroring HF exactly:
    /// right-pad to a multiple of <see cref="LogMelOptions.PadToMultipleOf"/>, semicausal left pad of frame/2 zeros,
    /// frame i covers padded samples [i*hop, i*hop+frame), window, 512-point rFFT, magnitude, mel matmul,
    /// log(mel + floor). A frame is valid only if its last sample (index i*hop + frame in the left-padded signal)
    /// is real audio; invalid frames are zeroed.
    /// </summary>
    public LogMelResult Extract(ReadOnlySpan<float> samples)
    {
        var mult = Math.Max(1, _o.PadToMultipleOf);
        var padded = (samples.Length + mult - 1) / mult * mult;
        var frames = FrameCount(padded);
        var mel = _o.MelBins;
        var features = new float[frames * mel];
        var mask = new bool[frames];
        if (frames == 0) return new LogMelResult(features, mask, 0);

        var padLeft = _o.FrameLength / 2;
        var fft = _o.FftLength;
        var re = new double[fft];
        var im = new double[fft];
        var mag = new double[_bins];

        for (var f = 0; f < frames; f++)
        {
            // Frame f starts at padded-left index f*hop, i.e. real sample index f*hop - padLeft.
            var origin = f * _o.HopLength - padLeft;
            Array.Clear(re);
            Array.Clear(im);
            for (var k = 0; k < _o.FrameLength; k++)
            {
                var idx = origin + k;
                if ((uint)idx < (uint)samples.Length) re[k] = (float)(samples[idx] * _window[k]);
            }

            // Valid iff the last sample of the unfold window (padded-left index f*hop + frame) is real audio.
            var lastReal = f * _o.HopLength + _o.FrameLength - padLeft;
            mask[f] = lastReal < samples.Length;
            if (!mask[f]) continue; // features stay 0, as HF multiplies by the mask

            Fft(re, im);
            for (var b = 0; b < _bins; b++) mag[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);

            var row = f * mel;
            for (var m = 0; m < mel; m++)
            {
                var w = _filterWeights[m];
                var start = _filterStart[m];
                double acc = 0;
                for (var j = 0; j < w.Length; j++) acc += mag[start + j] * w[j];
                features[row + m] = (float)Math.Log(acc + _o.MelFloor);
            }
        }

        return new LogMelResult(features, mask, frames);
    }

    /// <summary>Port of <c>window_function(frame_length)</c>: np.hanning(N+1)[:-1] if periodic else np.hanning(N).</summary>
    internal static double[] CreateHann(int length, bool periodic)
    {
        var denom = periodic ? length : length - 1; // np.hanning(M): 0.5 - 0.5 cos(2 pi n / (M-1))
        var w = new double[length];
        for (var n = 0; n < length; n++) w[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / denom);
        return w;
    }

    /// <summary>Port of <c>transformers.audio_utils.mel_filter_bank</c>. Returns [bins][mel].</summary>
    internal static double[][] CreateMelFilterBank(LogMelOptions o)
    {
        var bins = o.FftLength / 2 + 1;
        var nMel = o.MelBins;
        var melMin = HzToMel(o.MinFrequency, o.MelScale);
        var melMax = HzToMel(o.MaxFrequency, o.MelScale);
        var filterFreqs = new double[nMel + 2];
        for (var i = 0; i < filterFreqs.Length; i++)
            filterFreqs[i] = MelToHz(melMin + (melMax - melMin) * i / (nMel + 1), o.MelScale);

        // np.linspace(0, sampling_rate // 2, num_frequency_bins)
        var nyquist = o.SampleRate / 2;
        var fftFreqs = new double[bins];
        for (var b = 0; b < bins; b++) fftFreqs[b] = (double)nyquist * b / (bins - 1);

        var fb = new double[bins][];
        for (var b = 0; b < bins; b++)
        {
            fb[b] = new double[nMel];
            for (var m = 0; m < nMel; m++)
            {
                var down = -(filterFreqs[m] - fftFreqs[b]) / (filterFreqs[m + 1] - filterFreqs[m]);
                var up = (filterFreqs[m + 2] - fftFreqs[b]) / (filterFreqs[m + 2] - filterFreqs[m + 1]);
                fb[b][m] = Math.Max(0.0, Math.Min(down, up));
            }
        }

        if (o.SlaneyNormalization)
            for (var m = 0; m < nMel; m++)
            {
                var enorm = 2.0 / (filterFreqs[m + 2] - filterFreqs[m]);
                for (var b = 0; b < bins; b++) fb[b][m] *= enorm;
            }

        return fb;
    }

    internal static double HzToMel(double hz, MelScale scale)
    {
        if (scale == MelScale.Htk) return 2595.0 * Math.Log10(1.0 + hz / 700.0);
        const double minLogHz = 1000.0, minLogMel = 15.0;
        var logstep = 27.0 / Math.Log(6.4);
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) * logstep : 3.0 * hz / 200.0;
    }

    internal static double MelToHz(double mel, MelScale scale)
    {
        if (scale == MelScale.Htk) return 700.0 * (Math.Pow(10, mel / 2595.0) - 1.0);
        const double minLogHz = 1000.0, minLogMel = 15.0;
        var logstep = Math.Log(6.4) / 27.0;
        return mel >= minLogMel ? minLogHz * Math.Exp(logstep * (mel - minLogMel)) : 200.0 * mel / 3.0;
    }

    private static (int[] Start, double[][] Weights) Sparsify(double[][] fb)
    {
        var bins = fb.Length;
        var nMel = fb[0].Length;
        var starts = new int[nMel];
        var weights = new double[nMel][];
        for (var m = 0; m < nMel; m++)
        {
            int first = -1, last = -1;
            for (var b = 0; b < bins; b++)
                if (fb[b][m] != 0.0) { if (first < 0) first = b; last = b; }
            if (first < 0) { starts[m] = 0; weights[m] = []; continue; } // all-zero filter (HF warns, harmless)
            starts[m] = first;
            weights[m] = new double[last - first + 1];
            for (var b = first; b <= last; b++) weights[m][b - first] = fb[b][m];
        }
        return (starts, weights);
    }

    private static (double[] Cos, double[] Sin, int[] Rev) CreateFftTables(int n)
    {
        var cos = new double[n / 2];
        var sin = new double[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            cos[i] = Math.Cos(-2 * Math.PI * i / n);
            sin[i] = Math.Sin(-2 * Math.PI * i / n);
        }
        var bits = BitOperations.Log2((uint)n);
        var rev = new int[n];
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            rev[i] = r;
        }
        return (cos, sin, rev);
    }

    /// <summary>In-place iterative radix-2 decimation-in-time complex FFT of length FftLength (input here is real).</summary>
    private void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (var i = 0; i < n; i++)
        {
            var j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size >> 1;
            var step = n / size;
            for (var start = 0; start < n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
