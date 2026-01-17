using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pixelbadger.Toolkit.Rag.Components;

using Microsoft.Extensions.AI;

namespace Pixelbadger.Toolkit.Rag.Commands;

public class EvalCommand
{
    private readonly SearchIndexer _indexer;
    private readonly IChatClient _chatClient;

    public EvalCommand(SearchIndexer indexer, IChatClient chatClient)
    {
        _indexer = indexer;
        _chatClient = chatClient;
    }

    public Command Create()
    {
        var command = new Command("eval", "Run evaluation queries against the index and validate responses");

        var indexPathOption = new Option<string>(
            aliases: ["--index-path"],
            description: "Path to the Lucene.NET index directory")
        {
            IsRequired = true
        };

        var evalsPathOption = new Option<string?>(
            aliases: ["--evals-path"],
            description: "Path to the evals.json file (default: index-path/evals.json)")
        {
            IsRequired = false
        };

        var modesOption = new Option<string[]?>(
            aliases: ["--modes"],
            description: "Search modes to evaluate: 'bm25', 'vector', 'hybrid' (default: all)")
        {
            IsRequired = false
        };

        var maxResultsOption = new Option<int>(
            aliases: ["--max-results"],
            description: "Maximum number of results to retrieve per query")
        {
            IsRequired = false
        };
        maxResultsOption.SetDefaultValue(5);

        command.AddOption(indexPathOption);
        command.AddOption(evalsPathOption);
        command.AddOption(modesOption);
        command.AddOption(maxResultsOption);

        command.SetHandler(async (string indexPath, string? evalsPath, string[]? modes, int maxResults) =>
        {
            try
            {
                evalsPath ??= Path.Combine(indexPath, "evals.json");
                if (!File.Exists(evalsPath))
                {
                    throw new FileNotFoundException($"Evals file not found: {evalsPath}");
                }

                var evalsJson = await File.ReadAllTextAsync(evalsPath);
                var evals = JsonSerializer.Deserialize<List<EvalPair>>(evalsJson);
                if (evals == null || evals.Count == 0)
                {
                    throw new InvalidOperationException("No evaluation queries found");
                }

                modes ??= ["bm25", "vector", "hybrid"];

                var results = new List<EvalResult>();

                foreach (var eval in evals)
                {
                    Console.WriteLine($"Evaluating: {eval.Question}");

                    var evalResult = new EvalResult { Question = eval.Question, ExpectedAnswer = eval.ExpectedAnswer };

                    foreach (var mode in modes)
                    {
                        List<SearchResult> searchResults;
                        var searchMode = mode switch
                        {
                            "bm25" => SearchMode.Bm25,
                            "vector" => SearchMode.Vector,
                            "hybrid" => SearchMode.Hybrid,
                            _ => throw new InvalidOperationException($"Unknown mode: {mode}")
                        };
                        searchResults = await _indexer.SearchAsync(indexPath, eval.Question, searchMode, maxResults, null);

                        var combinedContent = string.Join("\n\n", searchResults.Select(r => r.Content));
                        var validationPrompt = $@"
Does the following response correctly answer the question?

Question: {eval.Question}
Expected Answer: {eval.ExpectedAnswer}

Response: {combinedContent}

Answer 'yes' or 'no' with a brief explanation.
";

                        var validationResponse = await _chatClient.GetResponseAsync(validationPrompt);
                        var validationText = validationResponse.Text ?? "";
                        var isCorrect = validationText.ToLower().Contains("yes");

                        evalResult.ModeResults[mode] = new ModeResult
                        {
                            IsCorrect = isCorrect,
                            Explanation = validationText,
                            RetrievedContent = combinedContent
                        };

                        Console.WriteLine($"  {mode}: {(isCorrect ? "✓" : "✗")}");
                    }

                    results.Add(evalResult);
                }

                // Output summary
                var summary = new
                {
                    TotalQueries = results.Count,
                    Modes = modes.Select(mode => new
                    {
                        Mode = mode,
                        Correct = results.Count(r => r.ModeResults[mode].IsCorrect),
                        Accuracy = (double)results.Count(r => r.ModeResults[mode].IsCorrect) / results.Count
                    })
                };

                Console.WriteLine("\nEvaluation Summary:");
                Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

                // Save detailed results
                var resultsPath = Path.Combine(indexPath, "eval-results.json");
                await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Detailed results saved to: {resultsPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption, evalsPathOption, modesOption, maxResultsOption);

        return command;
    }

    private record EvalPair(string Question, string ExpectedAnswer);

    private record EvalResult
    {
        public string Question { get; init; } = "";
        public string ExpectedAnswer { get; init; } = "";
        public Dictionary<string, ModeResult> ModeResults { get; } = new();
    }

    private record ModeResult
    {
        public bool IsCorrect { get; init; }
        public string Explanation { get; init; } = "";
        public string RetrievedContent { get; init; } = "";
    }
}