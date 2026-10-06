using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Commands;

/// <summary>The options shared by ingest, query and serve.</summary>
internal sealed class CommonOptions
{
    public Option<string> IndexPath { get; } = new("--index-path")
    {
        Description = "Path to the Lucene.NET (BM25) index directory",
        Required = true
    };

    public Option<string?> ConnectionString { get; } = new("--connection-string")
    {
        Description = $"SQL Server 2025 / Azure SQL connection string (falls back to the {SqlStoreOptions.ConnectionStringEnvVar} environment variable)"
    };

    public Option<string?> ModelPath { get; } = new("--model-path")
    {
        Description = $"Local directory holding the EmbeddingGemma 2 ONNX snapshot (falls back to the {EmbeddingModelOptions.ModelPathEnvVar} environment variable)"
    };

    public Option<bool> ExactVectorSearch { get; } = new("--exact-vector-search")
    {
        Description = "Always use exact VECTOR_DISTANCE instead of the approximate DiskANN vector index (useful on local SQL Server)"
    };

    public void AddTo(Command command)
    {
        command.Options.Add(IndexPath);
        command.Options.Add(ConnectionString);
        command.Options.Add(ModelPath);
        command.Options.Add(ExactVectorSearch);
    }

    public RagOptions Resolve(ParseResult parseResult, CliContext context, bool requireExistingIndex) =>
        RagOptionsResolver.Resolve(
            parseResult.GetValue(IndexPath),
            parseResult.GetValue(ConnectionString),
            parseResult.GetValue(ModelPath),
            parseResult.GetValue(ExactVectorSearch),
            context.GetEnvironmentVariable,
            requireExistingIndex);
}
