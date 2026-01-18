using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public interface IReranker
{
    List<SearchResult> RerankResults(List<SearchResult> bm25Results, List<SearchResult> vectorResults, int maxResults);
}
