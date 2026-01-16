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

        var enableVectorsOption = new Option<bool>(
            aliases: ["--enable-vectors"],
            description: "Enable vector storage using sqlite-vec for semantic search (requires OPENAI_API_KEY environment variable)")
        {
            IsRequired = false
        };
        enableVectorsOption.SetDefaultValue(false);

        command.AddOption(indexPathOption);
        command.AddOption(contentPathOption);
        command.AddOption(chunkingStrategyOption);
        command.AddOption(enableVectorsOption);

        command.SetHandler(async (string indexPath, string contentPath, string? chunkingStrategy, bool enableVectors) =>
        {
            try
            {
                var indexer = new SearchIndexer();

                if (enableVectors)
                {
                    var embeddingService = new OpenAIEmbeddingService();
                    indexer.SetEmbeddingService(embeddingService);
                }

                var options = new IngestOptions
                {
                    EnableVectorStorage = enableVectors
                };

                await indexer.IngestContentAsync(indexPath, contentPath, chunkingStrategy, options);

                var strategyUsed = chunkingStrategy ?? "auto-detected";
                var vectorStatus = enableVectors ? " with vector embeddings" : "";
                Console.WriteLine($"Successfully ingested content from '{contentPath}' into index at '{indexPath}' using {strategyUsed} chunking{vectorStatus}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption, contentPathOption, chunkingStrategyOption, enableVectorsOption);

        return command;
    }
}
