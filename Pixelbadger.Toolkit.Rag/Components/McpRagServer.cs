using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Pixelbadger.Toolkit.Rag.Components;

public class McpRagServer
{
    private static string _indexPath = string.Empty;
    private static SearchIndexer _searchIndexer;
    private static IEmbeddingService _embeddingService;

    public McpRagServer(string indexPath, SearchIndexer searchIndexer)
    {
        _indexPath = indexPath;
        _searchIndexer = searchIndexer;
        _embeddingService = searchIndexer.EmbeddingService;
    }

    public async Task RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpRagServer>();

        await builder.Build().RunAsync();
    }

    [McpServerTool, Description("Performs search against a Lucene.NET index using BM25 keyword search, vector semantic search, or hybrid search")]
    public static async Task<object?> Execute(
        [Description("The search query to be performed.")] string query,
        [Description("Maximum number of results to return (default: 5).")] int maxResults = 5,
        [Description("Optional array of source IDs to constrain search results to specific documents.")] string[]? sourceIds = null,
        [Description("Search mode: 'bm25' (keyword), 'vector' (semantic), or 'hybrid' (combined). Default: bm25")] string searchMode = "bm25")
    {
        if (string.IsNullOrEmpty(query))
        {
            return new { error = "Query is required" };
        }

        try
        {
            if (!Directory.Exists(_indexPath))
                return new { error = $"Index directory '{_indexPath}' not found." };

            var mode = ParseSearchMode(searchMode);

            var results = await _searchIndexer.SearchAsync(_indexPath, query, mode, maxResults, sourceIds);
            return new { content = FormatSearchResults(results, searchMode) };
        }
        catch (Exception ex)
        {
            return new { error = $"Search failed: {ex.Message}" };
        }
    }

    private static SearchMode ParseSearchMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "bm25" => SearchMode.Bm25,
            "vector" => SearchMode.Vector,
            "hybrid" => SearchMode.Hybrid,
            _ => SearchMode.Bm25 // Default to BM25 for unknown modes
        };
    }

    private static string FormatSearchResults(List<SearchResult> results, string searchMode = "bm25")
    {
        if (results.Count == 0)
            return "No relevant documents found for the query.";

        var response = $"Found {results.Count} relevant document(s) using {searchMode} search:\n\n";

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            response += $"Document {i + 1} (Score: {result.Score:F4})\n";
            response += $"Source: {result.SourceFile} (Paragraph {result.ParagraphNumber})\n";
            response += $"Source ID: {result.SourceId}\n";
            response += $"Content: {result.Content}\n";

            if (i < results.Count - 1)
                response += "\n" + new string('-', 60) + "\n\n";
        }

        return response;
    }
}
