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
    private static SearchIndexer _searchIndexer = new();

    public McpRagServer(string indexPath)
    {
        _indexPath = indexPath;
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

    [McpServerTool, Description("Performs BM25 similarity search against a Lucene.NET index")]
    public static async Task<object?> Execute(
        [Description("The search query to be performed.")] string query,
        [Description("Maximum number of results to return (default: 5).")] int maxResults = 5,
        [Description("Optional array of source IDs to constrain search results to specific documents.")] string[]? sourceIds = null)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new { error = "Query is required" };
        }

        try
        {
            if (!Directory.Exists(_indexPath))
                return new { error = $"Index directory '{_indexPath}' not found." };

            var results = await _searchIndexer.QueryAsync(_indexPath, query, maxResults, sourceIds);
            return new { content = FormatSearchResults(results) };
        }
        catch (Exception ex)
        {
            return new { error = $"Search failed: {ex.Message}" };
        }
    }

    private static string FormatSearchResults(List<SearchResult> results)
    {
        if (results.Count == 0)
            return "No relevant documents found for the query.";

        var response = $"Found {results.Count} relevant document(s):\n\n";

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
