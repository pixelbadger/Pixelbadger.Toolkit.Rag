using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Commands;

public class IngestCommand
{
    private readonly IContentIngester _ingester;

    public IngestCommand(IContentIngester ingester)
    {
        _ingester = ingester;
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

        command.AddOption(indexPathOption);
        command.AddOption(contentPathOption);

        command.SetHandler(async (string indexPath, string contentPath) =>
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
                    await _ingester.IngestFolderAsync(indexPath, contentPath, options);

                    Console.WriteLine($"Successfully ingested all supported files from folder '{contentPath}' into index at '{indexPath}' using semantic chunking with vector embeddings");
                }
                else if (File.Exists(contentPath))
                {
                    // Single file ingestion (backward compatibility)
                    await _ingester.IngestContentAsync(indexPath, contentPath, options);

                    Console.WriteLine($"Successfully ingested content from '{contentPath}' into index at '{indexPath}' using semantic chunking with vector embeddings");
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
        }, indexPathOption, contentPathOption);

        return command;
    }
}
