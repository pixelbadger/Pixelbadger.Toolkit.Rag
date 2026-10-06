using System.Security.Cryptography;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Text;

/// <summary>
/// Finds tokenizer.json files for the parity tests, most authoritative first:
/// <c>$PBRAG_MODEL_PATH/tokenizer.json</c>, <c>$PBRAG_TOKENIZER_PATH</c>,
/// <c>test-assets/tokenizer/tokenizer.json</c> (installed by tools/golden/fetch-test-tokenizer.sh).
/// </summary>
public static class TokenizerLocator
{
    public const string SkipReason = "No tokenizer.json available (set PBRAG_MODEL_PATH or PBRAG_TOKENIZER_PATH, or run tools/golden/fetch-test-tokenizer.sh)";

    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<string>();
        var model = TestModelPaths.ModelPath;
        if (model is not null) Add(Path.Combine(model, "tokenizer.json"));
        Add(Environment.GetEnvironmentVariable("PBRAG_TOKENIZER_PATH"));
        Add(Path.Combine(AppContext.BaseDirectory, "test-assets", "tokenizer", "tokenizer.json"));
        return found;

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !found.Contains(path)) found.Add(path);
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
