using Microsoft.Data.SqlTypes;

namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// One embedded unit of a document. Table: dbo.Chunks_EG2_256 (one table per model + dimension).
/// </summary>
public sealed class Chunk
{
    /// <summary>Int identity clustered primary key (required by the SQL Server vector index).</summary>
    public int ChunkId { get; set; }

    /// <summary>Unique, system-global chunk id.</summary>
    public Guid GlobalId { get; set; }

    /// <summary>FK to <see cref="Document.DocumentId"/> (cascade delete).</summary>
    public int DocumentId { get; set; }

    public Document? Document { get; set; }

    /// <summary>1-based position of the chunk within its document.</summary>
    public int Ordinal { get; set; }

    public Modality Modality { get; set; }

    /// <summary>Char offset (text) or milliseconds (audio). Null for images.</summary>
    public long? LocatorStart { get; set; }

    public long? LocatorEnd { get; set; }

    /// <summary>Raw chunk text (text chunks only). Null for image/audio.</summary>
    public string? ChunkText { get; set; }

    /// <summary>Embedding model identifier, e.g. "embeddinggemma-2@256".</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>Truncated (256-d) and re-normalised embedding. Column type vector(256).</summary>
    public SqlVector<float> Embedding { get; set; }
}
