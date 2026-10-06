using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

// STUB (Phase 0). Implemented by workstream E1.
public class SearchService : ISearchService
{
    public SearchService(RagOptions options, ILuceneRepository lucene, IDocumentStore store, IEmbeddingService embeddings, IReranker reranker) { }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string queryText, int maxResults = 10, IReadOnlyCollection<string>? sourceIds = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
