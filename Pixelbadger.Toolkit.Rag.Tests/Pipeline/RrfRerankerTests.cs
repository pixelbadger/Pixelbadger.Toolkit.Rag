using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

public class RrfRerankerTests
{
    private readonly RrfReranker _reranker = new();

    [Fact]
    public void Fuse_ReturnsEmpty_WhenBothListsEmpty()
    {
        _reranker.Fuse([], [], 10).Should().BeEmpty();
    }

    [Fact]
    public void Fuse_ReturnsEmpty_WhenMaxResultsNotPositive()
    {
        _reranker.Fuse([1], [2], 0).Should().BeEmpty();
    }

    [Fact]
    public void Fuse_KeywordOnly_RecordsKeywordRanksOnly()
    {
        var result = _reranker.Fuse([10, 20], [], 10);

        result.Select(r => r.ChunkId).Should().Equal(10, 20);
        result[0].KeywordRank.Should().Be(1);
        result[1].KeywordRank.Should().Be(2);
        result.Should().OnlyContain(r => r.VectorRank == null);
        result[0].Score.Should().BeApproximately(1f / 61, 1e-6f);
    }

    [Fact]
    public void Fuse_VectorOnly_RecordsVectorRanksOnly()
    {
        var result = _reranker.Fuse([], [10, 20], 10);

        result.Select(r => r.ChunkId).Should().Equal(10, 20);
        result.Should().OnlyContain(r => r.KeywordRank == null);
        result[1].VectorRank.Should().Be(2);
    }

    [Fact]
    public void Fuse_BoostsChunksInBothLists_AndSumsScores()
    {
        // 2 is rank 2 in keyword and rank 1 in vector: 1/62 + 1/61 beats either rank-1-only entry.
        var result = _reranker.Fuse([1, 2], [2, 3], 10);

        result[0].ChunkId.Should().Be(2);
        result[0].Score.Should().BeApproximately(1f / 62 + 1f / 61, 1e-6f);
        result[0].KeywordRank.Should().Be(2);
        result[0].VectorRank.Should().Be(1);
        result.Select(r => r.ChunkId).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void Fuse_TruncatesToMaxResults()
    {
        var result = _reranker.Fuse([1, 2, 3, 4], [5, 6, 7, 8], 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Fuse_IgnoresDuplicateIdsWithinOneList()
    {
        var result = _reranker.Fuse([1, 1, 2], [], 10);

        result.Should().HaveCount(2);
        result[0].Score.Should().BeApproximately(1f / 61, 1e-6f);
        result[0].KeywordRank.Should().Be(1);
    }

    [Fact]
    public void Fuse_BreaksTiesDeterministically_KeywordFirst()
    {
        var result = _reranker.Fuse([1], [2], 10);

        result.Select(r => r.ChunkId).Should().Equal(1, 2);
    }
}
