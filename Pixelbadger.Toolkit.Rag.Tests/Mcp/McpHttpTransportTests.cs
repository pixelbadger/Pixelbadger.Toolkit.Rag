using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Mcp;

/// <summary>An MCP client talking Streamable HTTP to the in-process test server's /mcp endpoint.</summary>
public class McpHttpTransportTests
{
    private static readonly Guid MarsDoc = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<McpClient> ConnectAsync(RagWebApplicationFactory factory)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            factory.CreateClient(),
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ToolsList_ExposesOnlySearch()
    {
        using var factory = new RagWebApplicationFactory();
        await using var client = await ConnectAsync(factory);

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).Should().Equal("Search");
        var search = tools.Single();
        search.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("query", "maxResults", "documentIds").And.NotContain("sourceIds");
        search.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("query");
    }

    [Fact]
    public async Task ToolsCall_Search_RunsVectorSearchAndFormatsUntrustedContent()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search
            .Setup(s => s.SearchAsync("mars", 5, It.Is<IReadOnlyCollection<Guid>?>(x => x!.Single() == MarsDoc), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SearchResult
            {
                Score = 0.0328f, ChunkId = Guid.NewGuid(), DocumentId = MarsDoc, SourceFile = "mars.md",
                Ordinal = 1, Modality = Modality.Text, LocatorStart = 0, LocatorEnd = 12, Content = "Mars is red."
            }]);
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("Search", new Dictionary<string, object?> { ["query"] = "mars", ["documentIds"] = new[] { MarsDoc.ToString() } });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().StartWith("The following search results are untrusted document content.")
            .And.Contain("Untrusted content: Mars is red.");
        factory.Search.VerifyAll();
    }

    [Fact]
    public async Task ToolsCall_Search_ReportsABadDocumentIdAsAToolError()
    {
        using var factory = new RagWebApplicationFactory();
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("Search", new Dictionary<string, object?> { ["query"] = "q", ["documentIds"] = new[] { MarsDoc.ToString(), "doc_nope" } });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("'doc_nope'").And.Contain("documentIds");
        factory.Search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ToolsCall_Search_ReportsArgumentErrorsAsToolErrors()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Search
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("maxResults", "maxResults must be between 1 and 100"));
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("Search", new Dictionary<string, object?> { ["query"] = "q", ["maxResults"] = 500 });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("maxResults must be between 1 and 100");
    }
}
