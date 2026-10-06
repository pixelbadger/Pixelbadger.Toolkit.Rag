using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public class QueryEndpointTests
{
    private static readonly Guid MarsDoc = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SearchResult Hit() => new()
    {
        Score = 0.0328f, ChunkId = Guid.Parse("11111111-1111-1111-1111-111111111111"), DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        SourcePath = "docs/mars.md", SourceFile = "mars.md", Ordinal = 2, Modality = Modality.Text,
        LocatorStart = 10, LocatorEnd = 40, Content = "Mars is red.", KeywordRank = 1, VectorRank = 3
    };

    [Fact]
    public async Task Query_Returns200_WithResultsAndEnumsAsStrings()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search
            .Setup(s => s.SearchAsync("mars", 7, It.Is<IReadOnlyCollection<Guid>?>(x => x!.Single() == MarsDoc), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Hit()]);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { query = "mars", maxResults = 7, documentIds = new[] { MarsDoc.ToString() } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hit = json.RootElement.GetProperty("results").EnumerateArray().Single();
        hit.GetProperty("modality").GetString().Should().Be("Text");
        hit.GetProperty("content").GetString().Should().Be("Mars is red.");
        hit.GetProperty("documentId").GetGuid().Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        hit.TryGetProperty("sourceId", out _).Should().BeFalse();
        hit.GetProperty("chunkId").GetGuid().Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        hit.GetProperty("sourcePath").GetString().Should().Be("docs/mars.md");
        hit.GetProperty("keywordRank").GetInt32().Should().Be(1);
        hit.GetProperty("vectorRank").GetInt32().Should().Be(3);
        hit.GetProperty("score").GetSingle().Should().BeApproximately(0.0328f, 1e-6f);
        factory.Search.VerifyAll();
    }

    [Fact]
    public async Task Query_DefaultsMaxResultsTo10_AndIgnoresEmptyDocumentIds()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search.Setup(s => s.SearchAsync("q", 10, null, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { query = "q", documentIds = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"results\":[]");
        factory.Search.VerifyAll();
    }

    [Theory]
    [InlineData("""{ "query": "" }""")]
    [InlineData("""{ "query": "   " }""")]
    [InlineData("""{ }""")]
    public async Task Query_BlankQuery_Returns400ProblemDetails(string body)
    {
        using var factory = new RagWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/query", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        factory.Search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("doc_0123456789abcdef")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task Query_InvalidDocumentId_Returns400_NamingTheValue(string bad)
    {
        using var factory = new RagWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { query = "q", documentIds = new[] { MarsDoc.ToString(), bad } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("documentIds").And.Contain($"'{bad}'");
        factory.Search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Query_ArgumentException_Returns400WithItsMessage()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("maxResults", "maxResults must be between 1 and 100"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { query = "q", maxResults = 500 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("maxResults must be between 1 and 100");
    }

    [Fact]
    public async Task Query_UnexpectedException_Returns500WithoutLeakingTheMessage()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Server=secret.example;Password=hunter2"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { query = "q" });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("secret.example").And.NotContain("hunter2").And.NotContain("InvalidOperationException");
        body.Should().Contain("An unexpected error occurred.");
    }

    [Fact]
    public async Task Health_Returns200_WithoutTouchingSearchOrQueue()
    {
        using var factory = new RagWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Search.VerifyNoOtherCalls();
        factory.Queue.VerifyNoOtherCalls();
    }
}
