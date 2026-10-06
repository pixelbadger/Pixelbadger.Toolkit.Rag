using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Dtos;

public sealed record IngestResult(string FilePath, string DocumentId, Modality Modality, int ChunkCount);

public sealed record IngestFailure(string FilePath, string Error);

public sealed record IngestSummary(IReadOnlyList<IngestResult> Succeeded, IReadOnlyList<IngestFailure> Failed, int Skipped);
