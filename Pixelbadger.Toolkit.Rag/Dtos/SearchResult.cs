using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>A vector search hit, hydrated from SQL.</summary>
public class SearchResult
{
    /// <summary>Cosine similarity (1 - cosine distance); higher is more similar.</summary>
    public float Score { get; set; }

    /// <summary>Chunk global id (unique per chunk).</summary>
    public Guid ChunkId { get; set; }

    /// <summary>Document id (assigned by the server when the document was created).</summary>
    public Guid DocumentId { get; set; }

    public string SourcePath { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>1-based chunk ordinal within the document.</summary>
    public int Ordinal { get; set; }

    public Modality Modality { get; set; }

    /// <summary>Char offset (text) or ms (audio); null for images.</summary>
    public long? LocatorStart { get; set; }
    public long? LocatorEnd { get; set; }

    /// <summary>Chunk text for text chunks; null for image/audio.</summary>
    public string? Content { get; set; }

}
