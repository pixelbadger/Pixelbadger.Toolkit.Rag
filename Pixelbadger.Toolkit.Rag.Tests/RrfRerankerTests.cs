using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class RrfRerankerTests
{
    private readonly RrfReranker _reranker = new();

    [Fact]
    public void RerankResults_ShouldReturnEmpty_WhenBothListsAreEmpty()
    {
        var result = _reranker.RerankResults(new List<SearchResult>(), new List<SearchResult>(), 10);

        result.Should().BeEmpty();
    }

    [Fact]
    public void RerankResults_ShouldReturnBm25Only_WhenVectorResultsAreEmpty()
    {
        var bm25 = new List<SearchResult>
        {
            MakeResult("doc1", 1.0f),
            MakeResult("doc2", 0.8f)
        };

        var result = _reranker.RerankResults(bm25, new List<SearchResult>(), 10);

        result.Should().HaveCount(2);
        result[0].DocumentId.Should().Be("doc1");
        result[1].DocumentId.Should().Be("doc2");
    }

    [Fact]
    public void RerankResults_ShouldReturnVectorOnly_WhenBm25ResultsAreEmpty()
    {
        var vector = new List<SearchResult>
        {
            MakeResult("doc1", 0.9f),
            MakeResult("doc2", 0.7f)
        };

        var result = _reranker.RerankResults(new List<SearchResult>(), vector, 10);

        result.Should().HaveCount(2);
        result[0].DocumentId.Should().Be("doc1");
        result[1].DocumentId.Should().Be("doc2");
    }

    [Fact]
    public void RerankResults_ShouldBoostDocumentsAppearingInBothLists()
    {
        var bm25 = new List<SearchResult>
        {
            MakeResult("doc_bm25_only", 1.0f),
            MakeResult("doc_both", 0.8f)
        };
        var vector = new List<SearchResult>
        {
            MakeResult("doc_vector_only", 0.9f),
            MakeResult("doc_both", 0.7f)
        };

        var result = _reranker.RerankResults(bm25, vector, 10);

        // "doc_both" appears in both lists, so it gets scores from both and should rank highest
        result[0].DocumentId.Should().Be("doc_both");
    }

    [Fact]
    public void RerankResults_ShouldRespectMaxResults()
    {
        var bm25 = new List<SearchResult>
        {
            MakeResult("doc1", 1.0f),
            MakeResult("doc2", 0.9f),
            MakeResult("doc3", 0.8f)
        };
        var vector = new List<SearchResult>
        {
            MakeResult("doc4", 0.9f),
            MakeResult("doc5", 0.8f),
            MakeResult("doc6", 0.7f)
        };

        var result = _reranker.RerankResults(bm25, vector, 2);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void RerankResults_ShouldOrderByDescendingFusedScore()
    {
        var bm25 = new List<SearchResult>
        {
            MakeResult("doc1", 1.0f),
            MakeResult("doc2", 0.5f),
            MakeResult("doc3", 0.3f)
        };
        var vector = new List<SearchResult>
        {
            MakeResult("doc3", 0.9f),
            MakeResult("doc2", 0.5f),
            MakeResult("doc1", 0.2f)
        };

        var result = _reranker.RerankResults(bm25, vector, 10);

        result.Should().BeInDescendingOrder(r => r.Score);
    }

    [Fact]
    public void RerankResults_ShouldAssignRrfScores_NotOriginalScores()
    {
        var bm25 = new List<SearchResult>
        {
            MakeResult("doc1", 5.0f) // Original score should be replaced
        };

        var result = _reranker.RerankResults(bm25, new List<SearchResult>(), 10);

        // RRF score for rank 1 = 1/(60+1) ≈ 0.0164
        result[0].Score.Should().NotBe(5.0f);
        result[0].Score.Should().BeApproximately(1.0f / 61.0f, 0.001f);
    }

    [Fact]
    public void RerankResults_ShouldHandleDuplicateDocumentIds_ByFusingScores()
    {
        // Both lists have the same document at rank 1
        var bm25 = new List<SearchResult> { MakeResult("doc1", 1.0f) };
        var vector = new List<SearchResult> { MakeResult("doc1", 0.9f) };

        var result = _reranker.RerankResults(bm25, vector, 10);

        result.Should().HaveCount(1);
        // Fused score: 1/(60+1) + 1/(60+1) = 2/(61) ≈ 0.0328
        result[0].Score.Should().BeApproximately(2.0f / 61.0f, 0.001f);
    }

    private static SearchResult MakeResult(string documentId, float score) => new()
    {
        DocumentId = documentId,
        Score = score,
        Content = $"Content of {documentId}",
        SourceFile = "test.txt",
        SourcePath = "/test.txt",
        SourceId = "test",
        ParagraphNumber = 1
    };
}
