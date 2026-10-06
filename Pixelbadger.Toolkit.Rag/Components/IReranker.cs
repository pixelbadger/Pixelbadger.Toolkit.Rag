namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>A fused ranking entry keyed by SQL ChunkId.</summary>
public sealed record FusedHit(int ChunkId, float Score, int? KeywordRank, int? VectorRank);

public interface IReranker
{
    /// <summary>Fuses two ranked chunk-id lists (best first) and returns the top <paramref name="maxResults"/>.</summary>
    IReadOnlyList<FusedHit> Fuse(IReadOnlyList<int> keywordRanking, IReadOnlyList<int> vectorRanking, int maxResults);
}
