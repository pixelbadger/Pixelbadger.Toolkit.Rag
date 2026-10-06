using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Persistence;

// STUB (Phase 0). Implemented by workstream D.
public sealed class SqlDocumentStore : IDocumentStore
{
    public SqlDocumentStore(SqlStoreOptions options) { }

    public Task MigrateAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ChunkRecord>> ReplaceDocumentAsync(DocumentDraft document, IReadOnlyList<ChunkDraft> chunks, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task SetIndexStatusAsync(string documentGlobalId, IndexStatus status, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryEmbedding, int maxResults, IReadOnlyCollection<string>? sourceIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyCollection<int> chunkIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
