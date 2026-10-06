using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Commands;

/// <summary>A configuration problem the user can fix (missing connection string, bad path...).</summary>
public sealed class CliConfigurationException(string message) : Exception(message);

/// <summary>Builds <see cref="RagOptions"/> from parsed CLI values with environment-variable fallbacks.</summary>
public static class RagOptionsResolver
{
    /// <param name="indexPath">Lucene index directory.</param>
    /// <param name="connectionString">--connection-string, else PBRAG_CONNECTION_STRING.</param>
    /// <param name="modelPath">--model-path, else PBRAG_MODEL_PATH.</param>
    /// <param name="exactVectorSearch">Force exact VECTOR_DISTANCE search.</param>
    /// <param name="getEnv">Environment accessor.</param>
    /// <param name="requireExistingIndex">True for query/serve: the Lucene index must already exist.</param>
    public static RagOptions Resolve(
        string? indexPath,
        string? connectionString,
        string? modelPath,
        bool exactVectorSearch,
        Func<string, string?> getEnv,
        bool requireExistingIndex)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
            throw new CliConfigurationException("Missing index path. Provide --index-path.");

        var connection = FirstNonBlank(connectionString, getEnv(SqlStoreOptions.ConnectionStringEnvVar));
        if (connection is null)
            throw new CliConfigurationException(
                $"Missing SQL Server connection string. Provide --connection-string or set the {SqlStoreOptions.ConnectionStringEnvVar} environment variable.");

        var model = FirstNonBlank(modelPath, getEnv(EmbeddingModelOptions.ModelPathEnvVar));
        if (model is null)
            throw new CliConfigurationException(
                $"Missing embedding model path. Provide --model-path or set the {EmbeddingModelOptions.ModelPathEnvVar} environment variable " +
                "(a local copy of onnx-community/embeddinggemma-2-ONNX; the tool never downloads models).");

        if (!Directory.Exists(model))
            throw new CliConfigurationException($"Model directory '{model}' not found.");

        if (requireExistingIndex && !Directory.Exists(indexPath))
            throw new CliConfigurationException($"Index directory '{indexPath}' not found. Run 'pbrag ingest' first.");

        return new RagOptions
        {
            IndexPath = indexPath,
            Sql = new SqlStoreOptions
            {
                ConnectionString = connection,
                SearchMode = exactVectorSearch ? VectorSearchMode.ExactOnly : VectorSearchMode.Auto
            },
            Model = new EmbeddingModelOptions { ModelPath = model }
        };
    }

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;
}
