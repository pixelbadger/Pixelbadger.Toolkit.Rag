using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Golden;

/// <summary>One tensor written by tools/golden/generate.mjs: <c>{case}.{name}.json</c> (dims, type) + <c>.bin.gz</c> (little-endian raw).</summary>
public sealed record GoldenTensor(int[] Dims, string Type, byte[] Bytes)
{
    public int Count => Dims.Aggregate(1, (a, b) => a * b);

    public float[] AsFloat()
    {
        Type.Should().Be("float32");
        var result = new float[Bytes.Length / 4];
        Buffer.BlockCopy(Bytes, 0, result, 0, Bytes.Length);
        return result;
    }

    public long[] AsInt64()
    {
        Type.Should().Be("int64");
        var result = new long[Bytes.Length / 8];
        Buffer.BlockCopy(Bytes, 0, result, 0, Bytes.Length);
        return result;
    }

    public bool[] AsBool() => Bytes.Select(b => b != 0).ToArray();
}

/// <summary>
/// Shared plumbing for the golden tests. They run only when <c>PBRAG_MODEL_PATH</c> points at a model snapshot AND
/// the fixtures from tools/golden are present (<c>manifest.json</c> marks a complete generation); otherwise they skip.
/// </summary>
public static class GoldenFixtures
{
    public static string Dir => TestModelPaths.GoldenDir;

    public static string RequireModelAndFixtures()
    {
        Skip.If(TestModelPaths.ModelPath is null, TestModelPaths.SkipReason);
        var manifest = Path.Combine(Dir, "manifest.json");
        Skip.IfNot(File.Exists(manifest), "golden fixtures not generated (see tools/golden/README.md)");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var dtype = doc.RootElement.TryGetProperty("dtype", out var d) ? d.GetString() : null;
        Skip.If(dtype != "q8", $"golden fixtures were generated with dtype '{dtype ?? "unknown"}', but the service uses the q8 graphs; regenerate with tools/golden/generate.mjs (default dtype q8)");
        return TestModelPaths.ModelPath!;
    }

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static GoldenTensor Tensor(string caseName, string tensor)
    {
        var baseName = Path.Combine(Dir, $"{caseName}.{tensor}");
        File.Exists(baseName + ".json").Should().BeTrue($"fixture {caseName}.{tensor} is missing; regenerate with tools/golden/generate.mjs (tensor names may need an ALIASES entry)");

        using var meta = JsonDocument.Parse(File.ReadAllBytes(baseName + ".json"));
        var dims = meta.RootElement.GetProperty("dims").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var type = meta.RootElement.GetProperty("type").GetString()!;

        using var gz = new GZipStream(File.OpenRead(baseName + ".bin.gz"), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return new GoldenTensor(dims, type, ms.ToArray());
    }

    public static bool Exists(string caseName, string tensor) => File.Exists(Path.Combine(Dir, $"{caseName}.{tensor}.json"));

    /// <summary>The real object graph for <paramref name="modelPath"/>, resolved through DI exactly like the web host does.</summary>
    public static ServiceProvider BuildServices(string modelPath)
    {
        var options = new RagOptions();
        options.Model.ModelPath = modelPath;
        return new ServiceCollection().AddRagServices(options).BuildServiceProvider();
    }

    public static IEmbeddingService Embeddings(IServiceProvider services) => services.GetRequiredService<IEmbeddingService>();
    public static IImagePreprocessor ImagePreprocessor(IServiceProvider services) => services.GetRequiredService<IImagePreprocessor>();
    public static IAudioPreprocessor AudioPreprocessor(IServiceProvider services) => services.GetRequiredService<IAudioPreprocessor>();

    /// <summary>Golden embeddings are full-width (768); the service returns 256 re-normalised. Compare like with like.</summary>
    public static float[] Truncated(float[] row768, int dimensions = 256) => EmbeddingMath.TruncateAndNormalize(row768, dimensions);

    public static double Cosine(float[] a, float[] b)
    {
        a.Length.Should().Be(b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return dot / Math.Sqrt(na * nb);
    }

    public static void AssertClose(float[] actual, float[] expected, float atol, string what)
    {
        actual.Length.Should().Be(expected.Length, $"{what}: length");
        var worst = 0f;
        var worstAt = -1;
        for (int i = 0; i < actual.Length; i++)
        {
            var d = Math.Abs(actual[i] - expected[i]);
            if (d > worst) { worst = d; worstAt = i; }
        }
        worst.Should().BeLessThanOrEqualTo(atol, $"{what}: max |diff| at flat index {worstAt} (actual {(worstAt >= 0 ? actual[worstAt] : 0)}, expected {(worstAt >= 0 ? expected[worstAt] : 0)})");
    }
}
