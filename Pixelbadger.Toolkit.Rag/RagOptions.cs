using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag;

/// <summary>
/// Per-invocation configuration, built by a command handler from CLI options / env vars
/// and passed to <c>AddRagServices(options)</c>.
/// </summary>
public sealed class RagOptions
{
    /// <summary>Lucene BM25 index directory.</summary>
    public string IndexPath { get; set; } = string.Empty;

    public SqlStoreOptions Sql { get; set; } = new();

    public EmbeddingModelOptions Model { get; set; } = new();
}
