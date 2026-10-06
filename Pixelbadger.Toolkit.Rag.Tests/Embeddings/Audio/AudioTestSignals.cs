namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Audio;

/// <summary>Deterministic synthetic signals; formulas mirror test-assets/audio/generate_reference.py.</summary>
internal static class AudioTestSignals
{
    public const int SampleRate = 16_000;

    public static float[] Sine(double hz, int samples, double amplitude = 0.5)
    {
        var x = new float[samples];
        for (var i = 0; i < samples; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * ((double)i / SampleRate)));
        return x;
    }

    public static float[] Tones(int n)
    {
        var x = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t) + 0.25 * Math.Sin(2 * Math.PI * 3000 * t));
        }
        return x;
    }

    public static float[] Chirp(int n)
    {
        var x = new float[n];
        var k = (7000.0 - 100.0) / ((double)n / SampleRate);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            x[i] = (float)(0.6 * Math.Sin(2 * Math.PI * (100 * t + 0.5 * k * t * t)));
        }
        return x;
    }

    public static float[] Short(int n) => Sine(1000, n, 0.3);

    /// <summary>Index of the HTK mel filter (0..127) whose centre is nearest to <paramref name="hz"/>.</summary>
    public static int ExpectedMelBin(double hz)
    {
        static double Mel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
        var step = Mel(8000) / 129.0; // 130 equally spaced points; the 128 filter centres are points 1..128
        return (int)Math.Round(Mel(hz) / step) - 1;
    }

    public static int ArgMax(ReadOnlySpan<float> row)
    {
        var best = 0;
        for (var i = 1; i < row.Length; i++) if (row[i] > row[best]) best = i;
        return best;
    }
}
