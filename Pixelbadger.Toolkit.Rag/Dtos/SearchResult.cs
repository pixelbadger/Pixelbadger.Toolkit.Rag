using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>A hybrid search hit, hydrated from SQL.</summary>
public class SearchResult
{
    /// <summary>Fused RRF score.</summary>
    public float Score { get; set; }

    /// <summary>Chunk global id (unique per chunk).</summary>
    public Guid ChunkId { get; set; }

    /// <summary>Document global id (e.g. "doc_…").</summary>
    public string DocumentId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;

    /// <summary>1-based chunk ordinal within the document.</summary>
    public int Ordinal { get; set; }

    public Modality Modality { get; set; }

    /// <summary>Char offset (text) or ms (audio); null for images.</summary>
    public long? LocatorStart { get; set; }
    public long? LocatorEnd { get; set; }

    /// <summary>Chunk text for text chunks; null for image/audio.</summary>
    public string? Content { get; set; }

    /// <summary>1-based rank in the BM25 list, null if absent from it.</summary>
    public int? KeywordRank { get; set; }

    /// <summary>1-based rank in the vector list, null if absent from it.</summary>
    public int? VectorRank { get; set; }
}
