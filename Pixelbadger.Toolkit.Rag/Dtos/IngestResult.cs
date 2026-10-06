using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// A file to ingest: the bytes on local disk (any path, typically a temp file that keeps the original
/// extension) and the logical path that identifies the document (see <see cref="DocumentIds.FromLogicalPath"/>).
/// </summary>
public sealed record IngestSource(string LocalPath, string LogicalPath);

/// <param name="FilePath">The logical path of the ingested file.</param>
public sealed record IngestResult(string FilePath, string DocumentId, Modality Modality, int ChunkCount);
