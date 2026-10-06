namespace Pixelbadger.Toolkit.Rag.Components;

// STUB (Phase 0). Implemented by workstream E1 (port of the previous Lucene BM25 code, keyed by chunk_id).
public class LuceneRepository : ILuceneRepository
{
    public Task ReplaceDocumentAsync(string indexPath, string documentGlobalId, string sourceId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<string>? sourceIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
