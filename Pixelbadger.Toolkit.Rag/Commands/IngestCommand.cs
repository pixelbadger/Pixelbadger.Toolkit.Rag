using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public class IngestCommand
{
    private readonly SearchIndexer _indexer;

    public IngestCommand(SearchIndexer indexer)
    {
        _indexer = indexer;
    }

    public Command Create()
    {
        var command = new Command("ingest", "Ingest content into a search index with intelligent chunking based on file type");

        var indexPathOption = new Option<string>(
            aliases: ["--index-path"],
            description: "Path to the Lucene.NET index directory")
        {
            IsRequired = true
        };

        var contentPathOption = new Option<string>(
            aliases: ["--content-path"],
            description: "Path to the content file or folder to ingest. If a folder is provided, all supported files (.txt, .md) will be ingested.")
        {
            IsRequired = true
        };

        var chunkingStrategyOption = new Option<string?>(
            aliases: ["--chunking-strategy"],
            description: "Chunking strategy: 'semantic', 'markdown', or 'paragraph' (default: auto-detect based on file extension)")
        {
            IsRequired = false
        };

        var evalsOption = new Option<int?>(
            aliases: ["--evals"],
            description: "Generate evaluation queries after ingestion (requires OPENAI_API_KEY). Number specifies how many query/answer pairs to generate.")
        {
            IsRequired = false
        };

        command.AddOption(indexPathOption);
        command.AddOption(contentPathOption);
        command.AddOption(chunkingStrategyOption);
        command.AddOption(evalsOption);

        command.SetHandler(async (string indexPath, string contentPath, string? chunkingStrategy, int? evalsCount) =>
        {
            try
            {
                var options = new IngestOptions
                {
                    EnableVectorStorage = true
                };

                // Check if contentPath is a directory or file
                if (Directory.Exists(contentPath))
                {
                    // Folder-based ingestion
                    await _indexer.IngestFolderAsync(indexPath, contentPath, chunkingStrategy, options);

                    var strategyUsed = chunkingStrategy ?? "auto-detected";
                    Console.WriteLine($"Successfully ingested all supported files from folder '{contentPath}' into index at '{indexPath}' using {strategyUsed} chunking with vector embeddings");
                }
                else if (File.Exists(contentPath))
                {
                    // Single file ingestion (backward compatibility)
                    await _indexer.IngestContentAsync(indexPath, contentPath, chunkingStrategy, options);

                    var strategyUsed = chunkingStrategy ?? "auto-detected";
                    Console.WriteLine($"Successfully ingested content from '{contentPath}' into index at '{indexPath}' using {strategyUsed} chunking with vector embeddings");

                    if (evalsCount.HasValue && evalsCount.Value > 0)
                    {
                        Console.WriteLine($"Generating {evalsCount.Value} evaluation queries...");
                        await GenerateEvaluationQueriesAsync(indexPath, contentPath, evalsCount.Value);
                        Console.WriteLine($"Evaluation queries saved to '{Path.Combine(indexPath, "evals.json")}'");
                    }
                }
                else
                {
                    throw new FileNotFoundException($"Path not found: {contentPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption, contentPathOption, chunkingStrategyOption, evalsOption);

        return command;
    }

    private static async Task GenerateEvaluationQueriesAsync(string indexPath, string contentPath, int count)
    {
        var content = await File.ReadAllTextAsync(contentPath);
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY environment variable is required for eval generation");
        }

        var openAIClient = new OpenAI.OpenAIClient(apiKey);
        IChatClient chatClient = openAIClient.GetChatClient("gpt-4o-mini").AsIChatClient();

        var prompt = $@"
Generate {count} diverse questions that can be answered using the information in the following document.
For each question, also provide the expected answer based on the document content.

Format the output as a JSON array of objects, each with 'question' and 'expectedAnswer' fields.

Document content:
{content}
";

        var response = await chatClient.GetResponseAsync(prompt);
        var jsonText = response.Text ?? "[]";

        // Parse and validate JSON
        var evals = JsonSerializer.Deserialize<List<EvalPair>>(jsonText);
        if (evals == null || evals.Count == 0)
        {
            throw new InvalidOperationException("Failed to generate evaluation queries");
        }

        // Limit to requested count
        evals = evals.Take(count).ToList();

        var evalsPath = Path.Combine(indexPath, "evals.json");
        await File.WriteAllTextAsync(evalsPath, JsonSerializer.Serialize(evals, new JsonSerializerOptions { WriteIndented = true }));
    }

    private record EvalPair(string Question, string ExpectedAnswer);
}
