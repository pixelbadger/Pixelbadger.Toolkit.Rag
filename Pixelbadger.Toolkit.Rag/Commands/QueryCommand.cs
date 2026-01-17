using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public class QueryCommand
{
    private readonly SearchIndexer _indexer;

    public QueryCommand(SearchIndexer indexer)
    {
        _indexer = indexer;
    }

    public Command Create()
    {
        var command = new Command("query", "Perform search against an index using BM25, vector, or hybrid modes");

        var indexPathOption = new Option<string>(
            aliases: ["--index-path"],
            description: "Path to the Lucene.NET index directory")
        {
            IsRequired = true
        };

        var queryOption = new Option<string>(
            aliases: ["--query"],
            description: "Search query text")
        {
            IsRequired = true
        };

        var maxResultsOption = new Option<int>(
            aliases: ["--max-results"],
            description: "Maximum number of results to return")
        {
            IsRequired = false
        };
        maxResultsOption.SetDefaultValue(10);

        var sourceIdsOption = new Option<string[]>(
            aliases: ["--sourceIds"],
            description: "Optional list of source IDs to constrain search results")
        {
            IsRequired = false
        };

        var searchModeOption = new Option<string>(
            aliases: ["--search-mode"],
            description: "Search mode: 'bm25' (keyword), 'vector' (semantic), or 'hybrid' (combined)")
        {
            IsRequired = false
        };
        searchModeOption.SetDefaultValue("bm25");

        command.AddOption(indexPathOption);
        command.AddOption(queryOption);
        command.AddOption(maxResultsOption);
        command.AddOption(sourceIdsOption);
        command.AddOption(searchModeOption);

        command.SetHandler(async (string indexPath, string query, int maxResults, string[] sourceIds, string searchModeStr) =>
        {
            try
            {
                var searchMode = ParseSearchMode(searchModeStr);
                var results = await _indexer.SearchAsync(indexPath, query, searchMode, maxResults, sourceIds);

                if (results.Count == 0)
                {
                    Console.WriteLine("No results found.");
                    return;
                }

                Console.WriteLine($"Found {results.Count} result(s) using {searchModeStr} search:");
                Console.WriteLine();

                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i];
                    Console.WriteLine($"Result {i + 1} (Score: {result.Score:F4})");
                    Console.WriteLine($"Source: {result.SourceFile} (Paragraph {result.ParagraphNumber})");
                    Console.WriteLine($"Content: {result.Content}");

                    if (i < results.Count - 1)
                    {
                        Console.WriteLine(new string('-', 60));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption, queryOption, maxResultsOption, sourceIdsOption, searchModeOption);

        return command;
    }

    private static SearchMode ParseSearchMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "bm25" => SearchMode.Bm25,
            "vector" => SearchMode.Vector,
            "hybrid" => SearchMode.Hybrid,
            _ => throw new ArgumentException($"Unknown search mode: {mode}. Valid modes are: bm25, vector, hybrid")
        };
    }
}
