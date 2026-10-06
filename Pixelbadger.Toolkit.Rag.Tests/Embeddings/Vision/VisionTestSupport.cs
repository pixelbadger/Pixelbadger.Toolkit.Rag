using System.Text.Json;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

/// <summary>Fixture access and the deterministic test image shared with test-assets/vision/generate_fixtures.py.</summary>
internal static class VisionTestSupport
{
    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "test-assets", "vision");

    public static string Asset(params string[] parts) => Path.Combine([AssetsDir, .. parts]);

    /// <summary>Same formula as <c>synth()</c> in generate_fixtures.py. Interleaved RGB.</summary>
    public static byte[] SynthRgb(int w, int h)
    {
        var a = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                a[i] = (byte)((x * 37 + y * 11) & 255);
                a[i + 1] = (byte)((y * 91 + (x * y) % 251) & 255);
                a[i + 2] = (byte)(((x ^ y) * 53 + 7) & 255);
            }
        return a;
    }

    public static JsonElement LoadJson(string file) => JsonDocument.Parse(File.ReadAllText(Asset(file))).RootElement;

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
}
