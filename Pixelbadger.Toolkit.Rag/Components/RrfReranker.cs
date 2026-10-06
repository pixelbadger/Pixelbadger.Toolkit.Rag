namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Reciprocal Rank Fusion over chunk ids (k = 60).</summary>
public class RrfReranker : IReranker
{
    /// <summary>Standard RRF constant.</summary>
    public const int K = 60;

    /// <inheritdoc />
    public IReadOnlyList<FusedHit> Fuse(IReadOnlyList<int> keywordRanking, IReadOnlyList<int> vectorRanking, int maxResults)
    {
        if (maxResults < 1)
        {
            return Array.Empty<FusedHit>();
        }

        // Insertion order (keyword list first) gives a deterministic tie-break.
        var scores = new Dictionary<int, (float Score, int? KeywordRank, int? VectorRank)>();
        var order = new List<int>();

        Accumulate(keywordRanking, isKeyword: true);
        Accumulate(vectorRanking, isKeyword: false);

        return order
            .Select(id => new FusedHit(id, scores[id].Score, scores[id].KeywordRank, scores[id].VectorRank))
            .OrderByDescending(h => h.Score)
            .Take(maxResults)
            .ToList();

        void Accumulate(IReadOnlyList<int> ranking, bool isKeyword)
        {
            for (int i = 0; i < ranking.Count; i++)
            {
                var id = ranking[i];
                var rank = i + 1;
                var rrf = 1.0f / (K + rank);

                if (!scores.TryGetValue(id, out var entry))
                {
                    order.Add(id);
                    entry = (0f, null, null);
                }

                // A duplicate id within one list keeps its best (first) rank and is not double counted.
                if (isKeyword)
                {
                    if (entry.KeywordRank.HasValue) continue;
                    entry = (entry.Score + rrf, rank, entry.VectorRank);
                }
                else
                {
                    if (entry.VectorRank.HasValue) continue;
                    entry = (entry.Score + rrf, entry.KeywordRank, rank);
                }

                scores[id] = entry;
            }
        }
    }
}
