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

        var noVectorsOption = new Option<bool>(
            aliases: ["--no-vectors"],
            description: "Disable vector storage and avoid sending document content to OpenAI for embeddings.");

        var maxFileSizeOption = new Option<long>(
            aliases: ["--max-file-size-bytes"],
            description: "Maximum size in bytes for a single ingested file.");
        maxFileSizeOption.SetDefaultValue(IngestOptions.DefaultMaxFileSizeBytes);

        var maxFilesOption = new Option<int>(
            aliases: ["--max-files"],
            description: "Maximum number of supported files to ingest from a folder.");
        maxFilesOption.SetDefaultValue(IngestOptions.DefaultMaxFiles);

        var maxChunkCharactersOption = new Option<int>(
            aliases: ["--max-chunk-characters"],
            description: "Maximum size in characters for a single chunk before indexing or embedding.");
        maxChunkCharactersOption.SetDefaultValue(IngestOptions.DefaultMaxChunkCharacters);

        var allowSymlinksOption = new Option<bool>(
            aliases: ["--allow-symlinks"],
            description: "Allow ingestion of symbolic links and reparse points. Disabled by default to avoid indexing off-root files.");

        command.AddOption(indexPathOption);
        command.AddOption(contentPathOption);
        command.AddOption(noVectorsOption);
        command.AddOption(maxFileSizeOption);
        command.AddOption(maxFilesOption);
        command.AddOption(maxChunkCharactersOption);
        command.AddOption(allowSymlinksOption);

        command.SetHandler(async (
            string indexPath,
            string contentPath,
            bool noVectors,
            long maxFileSizeBytes,
            int maxFiles,
            int maxChunkCharacters,
            bool allowSymlinks) =>
        {
            try
            {
                var options = new IngestOptions
                {
                    EnableVectorStorage = !noVectors,
                    MaxFileSizeBytes = maxFileSizeBytes,
                    MaxFiles = maxFiles,
                    MaxChunkCharacters = maxChunkCharacters,
                    AllowSymlinks = allowSymlinks
                };

                var storageMode = options.EnableVectorStorage
                    ? "BM25 and vector storage. Document content will be sent to OpenAI for embeddings"
                    : "BM25 storage only";

                // Check if contentPath is a directory or file
                if (Directory.Exists(contentPath))
                {
                    // Folder-based ingestion
                    await _ingester.IngestFolderAsync(indexPath, contentPath, options);

                    Console.WriteLine($"Successfully ingested all supported files from folder '{contentPath}' into index at '{indexPath}' using {storageMode}");
                }
                else if (File.Exists(contentPath))
                {
                    // Single file ingestion (backward compatibility)
                    await _ingester.IngestContentAsync(indexPath, contentPath, options);

                    Console.WriteLine($"Successfully ingested content from '{contentPath}' into index at '{indexPath}' using {storageMode}");
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
        }, indexPathOption, contentPathOption, noVectorsOption, maxFileSizeOption, maxFilesOption, maxChunkCharactersOption, allowSymlinksOption);

        return command;
    }
}
