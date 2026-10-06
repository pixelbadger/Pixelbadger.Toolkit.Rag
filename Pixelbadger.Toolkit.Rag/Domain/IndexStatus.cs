namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// Ingestion lifecycle of a document. Persisted as tinyint.
/// </summary>
public enum IndexStatus : byte
{
    Queued = 0,
    Processing = 1,
    Indexed = 2,
    Failed = 3
}
