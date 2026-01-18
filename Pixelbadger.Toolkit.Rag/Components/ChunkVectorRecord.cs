using Microsoft.Extensions.VectorData;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Data model for storing chunk embeddings in the vector store.
/// </summary>
public class ChunkVectorRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceFile { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourcePath { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceId { get; set; } = string.Empty;

    [VectorStoreData]
    public int ChunkNumber { get; set; }

    [VectorStoreData]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreVector(3072, DistanceFunction = DistanceFunction.EuclideanDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
