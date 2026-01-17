using System.CommandLine;
using System.Text.Json;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public class IngestCommand
{
    private readonly SearchIndexer _indexer;
    private readonly EvalGenerator _evalGenerator;

    public IngestCommand(SearchIndexer indexer, EvalGenerator evalGenerator)
    {
        _indexer = indexer;
        _evalGenerator = evalGenerator;
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
                        var content = await File.ReadAllTextAsync(contentPath);
                        var evals = await _evalGenerator.GenerateAsync(content, evalsCount.Value);

                        var evalsPath = Path.Combine(indexPath, "evals.json");
                        await File.WriteAllTextAsync(evalsPath, JsonSerializer.Serialize(evals, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine($"Evaluation queries saved to '{evalsPath}'");
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
}
