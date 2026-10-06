using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Mcp;

/// <summary>
/// MCP tool surface. Instances are created per call by the MCP host's DI container, so the search
/// service is resolved per call (no static state).
/// </summary>
[McpServerToolType]
public sealed class McpRagServer(ISearchService searchService, ILogger<McpRagServer> logger)
{
    [McpServerTool(Name = "Search"), Description("Hybrid search (BM25 keyword + semantic vector, fused with RRF) over indexed text, image and audio documents.")]
    public async Task<CallToolResult> Search(
        [Description("The search query to be performed.")] string query,
        [Description("Maximum number of results to return (default: 5).")] int maxResults = 5,
        [Description("Optional array of document IDs (GUIDs) to constrain search results to specific documents.")] string[]? documentIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Error("Query is required");

        try
        {
            var results = await searchService.SearchAsync(query, maxResults, DocumentIdFilter.Parse(documentIds), cancellationToken);
            return Text(SearchResultFormatter.FormatForMcp(results));
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search failed");
            return Error("Search failed. Check server logs for details.");
        }
    }

    private static CallToolResult Text(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
