using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public class RrfReranker : IReranker
{
    public List<SearchResult> RerankResults(List<SearchResult> bm25Results, List<SearchResult> vectorResults, int maxResults)
    {
        // Apply Reciprocal Rank Fusion (RRF)
        // RRF score = sum(1 / (k + rank)) for each result list
        const int k = 60; // Standard RRF constant

        var fusedScores = new Dictionary<string, (float Score, SearchResult Result)>();

        // Process BM25 results
        for (int i = 0; i < bm25Results.Count; i++)
        {
            var result = bm25Results[i];
            var rrfScore = 1.0f / (k + i + 1);

            if (fusedScores.TryGetValue(result.DocumentId, out var existing))
            {
                fusedScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
            }
            else
            {
                fusedScores[result.DocumentId] = (rrfScore, result);
            }
        }

        // Process vector results
        for (int i = 0; i < vectorResults.Count; i++)
        {
            var result = vectorResults[i];
            var rrfScore = 1.0f / (k + i + 1);

            if (fusedScores.TryGetValue(result.DocumentId, out var existing))
            {
                fusedScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
            }
            else
            {
                fusedScores[result.DocumentId] = (rrfScore, result);
            }
        }

        // Sort by fused score and return top results
        return fusedScores.Values
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x =>
            {
                x.Result.Score = x.Score;
                return x.Result;
            })
            .ToList();
    }
}