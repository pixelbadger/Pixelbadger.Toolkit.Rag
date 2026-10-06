namespace Pixelbadger.Toolkit.Rag.Persistence;

public enum VectorSearchMode
{
    /// <summary>Approximate VECTOR_SEARCH when the index exists; exact otherwise.</summary>
    Auto,
    /// <summary>Always exact VECTOR_DISTANCE (local SQL Server where the preview index lags).</summary>
    ExactOnly
}

public sealed class SqlStoreOptions
{
    public const string ConnectionStringEnvVar = "PBRAG_CONNECTION_STRING";

    public string ConnectionString { get; set; } = string.Empty;

    public VectorSearchMode SearchMode { get; set; } = VectorSearchMode.Auto;
}
