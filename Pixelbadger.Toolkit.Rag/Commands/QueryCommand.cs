using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class QueryCommand
{
    public static Command Create()
    {
        var command = new Command("query", "Perform BM25 similarity search against a Lucene.NET index");

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

        command.AddOption(indexPathOption);
        command.AddOption(queryOption);
        command.AddOption(maxResultsOption);
        command.AddOption(sourceIdsOption);

        command.SetHandler(async (string indexPath, string query, int maxResults, string[] sourceIds) =>
        {
            try
            {
                var indexer = new SearchIndexer();
                var results = await indexer.QueryAsync(indexPath, query, maxResults, sourceIds);

                if (results.Count == 0)
                {
                    Console.WriteLine("No results found.");
                    return;
                }

                Console.WriteLine($"Found {results.Count} result(s):");
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
        }, indexPathOption, queryOption, maxResultsOption, sourceIdsOption);

        return command;
    }
}
