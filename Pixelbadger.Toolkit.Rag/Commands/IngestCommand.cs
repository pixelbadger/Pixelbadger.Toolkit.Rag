using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class IngestCommand
{
    public static Command Create()
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
            description: "Path to the content file to ingest")
        {
            IsRequired = true
        };

        var chunkingStrategyOption = new Option<string?>(
            aliases: ["--chunking-strategy"],
            description: "Chunking strategy: 'semantic', 'markdown', or 'paragraph' (default: auto-detect based on file extension)")
        {
            IsRequired = false
        };

        command.AddOption(indexPathOption);
        command.AddOption(contentPathOption);
        command.AddOption(chunkingStrategyOption);

        command.SetHandler(async (string indexPath, string contentPath, string? chunkingStrategy) =>
        {
            try
            {
                var indexer = new SearchIndexer();

                var embeddingService = new OpenAIEmbeddingService();
                indexer.SetEmbeddingService(embeddingService);

                var options = new IngestOptions
                {
                    EnableVectorStorage = true
                };

                await indexer.IngestContentAsync(indexPath, contentPath, chunkingStrategy, options);

                var strategyUsed = chunkingStrategy ?? "auto-detected";
                Console.WriteLine($"Successfully ingested content from '{contentPath}' into index at '{indexPath}' using {strategyUsed} chunking with vector embeddings");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption, contentPathOption, chunkingStrategyOption);

        return command;
    }
}
