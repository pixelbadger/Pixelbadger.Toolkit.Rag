using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// A file to ingest: the bytes on local disk (any path, typically a temp file that keeps the original
/// extension), the logical path (metadata only) and the id of the existing document the chunks belong to.
/// </summary>
public sealed record IngestSource(string LocalPath, string LogicalPath, Guid DocumentId);

/// <param name="FilePath">The logical path of the ingested file.</param>
public sealed record IngestResult(string FilePath, Guid DocumentId, Modality Modality, int ChunkCount);
