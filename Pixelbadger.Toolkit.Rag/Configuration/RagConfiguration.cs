using Microsoft.Extensions.Configuration;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Configuration;

/// <summary>A configuration problem the operator can fix (missing connection string, bad path...).</summary>
public sealed class RagConfigurationException(string message) : Exception(message);

/// <summary>
/// Builds and validates <see cref="RagOptions"/> from the <c>Rag</c> configuration section (appsettings,
/// <c>Rag__*</c> environment variables, command-line), falling back to the <c>PBRAG_*</c> variables.
/// Validation only checks presence and that the model directory exists: it never loads the model or opens SQL.
/// </summary>
public static class RagConfiguration
{
    public const string SectionName = "Rag";
    public const string IndexPathEnvVar = "PBRAG_INDEX_PATH";

    /// <param name="configuration">
    /// Application configuration. Environment variables are part of it, so the <c>PBRAG_*</c> fallbacks are
    /// looked up here too (and can be supplied by any other provider).
    /// </param>
    /// <exception cref="RagConfigurationException">A required value is missing or invalid.</exception>
    public static RagOptions Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        var indexPath = FirstNonBlank(section["IndexPath"], configuration[IndexPathEnvVar])
            ?? throw new RagConfigurationException(
                $"Missing index path. Set Rag:IndexPath (Rag__IndexPath) or the {IndexPathEnvVar} environment variable.");

        var connection = FirstNonBlank(section["ConnectionString"], configuration[SqlStoreOptions.ConnectionStringEnvVar])
            ?? throw new RagConfigurationException(
                $"Missing SQL Server connection string. Set Rag:ConnectionString (Rag__ConnectionString) or the {SqlStoreOptions.ConnectionStringEnvVar} environment variable.");

        var model = FirstNonBlank(section["ModelPath"], configuration[EmbeddingModelOptions.ModelPathEnvVar])
            ?? throw new RagConfigurationException(
                $"Missing embedding model path. Set Rag:ModelPath (Rag__ModelPath) or the {EmbeddingModelOptions.ModelPathEnvVar} environment variable " +
                "(a local copy of onnx-community/embeddinggemma-2-ONNX; the service never downloads models).");

        if (!Directory.Exists(model))
            throw new RagConfigurationException($"Model directory '{model}' not found.");

        var ingest = new IngestSettings();
        var exact = false;
        var migrate = true;
        try
        {
            section.GetSection("Ingest").Bind(ingest);
            exact = section.GetValue("ExactVectorSearch", false);
            migrate = section.GetValue("ApplyMigrationsOnStartup", true);
        }
        catch (InvalidOperationException ex)
        {
            throw new RagConfigurationException($"Invalid configuration value in the '{SectionName}' section: {ex.InnerException?.Message ?? ex.Message}");
        }

        ValidateIngest(ingest);

        try
        {
            Directory.CreateDirectory(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RagConfigurationException($"Index directory '{indexPath}' could not be created: {ex.Message}");
        }

        return new RagOptions
        {
            IndexPath = indexPath,
            Sql = new SqlStoreOptions
            {
                ConnectionString = connection,
                SearchMode = exact ? VectorSearchMode.ExactOnly : VectorSearchMode.Auto
            },
            Model = new EmbeddingModelOptions { ModelPath = model },
            Ingest = ingest,
            ApplyMigrationsOnStartup = migrate
        };
    }

    private static void ValidateIngest(IngestSettings ingest)
    {
        RequirePositive(ingest.MaxFileSizeBytes, nameof(ingest.MaxFileSizeBytes));
        RequirePositive(ingest.MaxFilesPerJob, nameof(ingest.MaxFilesPerJob));
        RequirePositive(ingest.MaxChunkCharacters, nameof(ingest.MaxChunkCharacters));
        RequirePositive(ingest.MaxAttempts, nameof(ingest.MaxAttempts));
        RequirePositive(ingest.LeaseSeconds, nameof(ingest.LeaseSeconds));
        RequirePositive(ingest.PollIntervalSeconds, nameof(ingest.PollIntervalSeconds));
    }

    private static void RequirePositive(long value, string name)
    {
        if (value < 1)
            throw new RagConfigurationException($"Rag:Ingest:{name} must be greater than zero.");
    }

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;
}
