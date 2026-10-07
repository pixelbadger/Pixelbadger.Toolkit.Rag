using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag;

/// <summary>
/// Application configuration, built from the <c>Rag</c> configuration section by
/// <see cref="Configuration.RagConfiguration"/> and passed to <c>AddRagServices(options)</c>.
/// </summary>
public sealed class RagOptions
{
    public SqlStoreOptions Sql { get; set; } = new();

    public EmbeddingModelOptions Model { get; set; } = new();

    public IngestSettings Ingest { get; set; } = new();

    /// <summary>Apply EF Core migrations when the host starts, before the message bus starts.</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}
