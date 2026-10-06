using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class IngestCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("ingest", "Ingest text (.txt, .md), image and audio files into the SQL Server + Lucene hybrid index");
        var common = new CommonOptions();
        common.AddTo(command);

        var contentPath = new Option<string>("--content-path")
        {
            Description = "Path to a file or folder to ingest. Folders are scanned recursively for supported files.",
            Required = true
        };
        var maxFileSize = new Option<long>("--max-file-size-bytes")
        {
            Description = "Maximum size in bytes for a single ingested file.",
            DefaultValueFactory = _ => IngestOptions.DefaultMaxFileSizeBytes
        };
        var maxFiles = new Option<int>("--max-files")
        {
            Description = "Maximum number of supported files to ingest from a folder.",
            DefaultValueFactory = _ => IngestOptions.DefaultMaxFiles
        };
        var maxChunkCharacters = new Option<int>("--max-chunk-characters")
        {
            Description = "Maximum size in characters for a single text chunk before indexing or embedding.",
            DefaultValueFactory = _ => IngestOptions.DefaultMaxChunkCharacters
        };
        var allowSymlinks = new Option<bool>("--allow-symlinks")
        {
            Description = "Allow ingestion of symbolic links and reparse points. Disabled by default to avoid indexing off-root files."
        };

        command.Options.Add(contentPath);
        command.Options.Add(maxFileSize);
        command.Options.Add(maxFiles);
        command.Options.Add(maxChunkCharacters);
        command.Options.Add(allowSymlinks);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var ragOptions = common.Resolve(parseResult, context, requireExistingIndex: false);
                var path = parseResult.GetValue(contentPath)!;
                var ingestOptions = new IngestOptions
                {
                    MaxFileSizeBytes = parseResult.GetValue(maxFileSize),
                    MaxFiles = parseResult.GetValue(maxFiles),
                    MaxChunkCharacters = parseResult.GetValue(maxChunkCharacters),
                    AllowSymlinks = parseResult.GetValue(allowSymlinks)
                };

                var isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path))
                    throw new FileNotFoundException($"Path not found: {path}");

                var provider = context.BuildServices(ragOptions);
                try
                {
                    await provider.GetRequiredService<IDocumentStore>().MigrateAsync(cancellationToken);
                    var ingester = provider.GetRequiredService<IContentIngester>();

                    IngestSummary summary;
                    if (isFolder)
                    {
                        summary = await ingester.IngestFolderAsync(path, ingestOptions, cancellationToken);
                    }
                    else
                    {
                        var result = await ingester.IngestFileAsync(path, ingestOptions, cancellationToken);
                        summary = new IngestSummary([result], [], 0);
                    }

                    WriteSummary(context.Out, summary, ragOptions.IndexPath);
                    return summary.Failed.Count > 0 ? 1 : 0;
                }
                finally
                {
                    await DisposeAsync(provider);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await context.Err.WriteLineAsync("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                await context.Err.WriteLineAsync($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    internal static void WriteSummary(TextWriter output, IngestSummary summary, string indexPath)
    {
        foreach (var ok in summary.Succeeded)
            output.WriteLine($"  OK    {ok.FilePath}  [{ok.Modality}, {ok.ChunkCount} chunk(s), {ok.DocumentId}]");
        foreach (var failure in summary.Failed)
            output.WriteLine($"  FAIL  {failure.FilePath}: {failure.Error}");

        output.WriteLine(
            $"Ingested {summary.Succeeded.Count} file(s), {summary.Failed.Count} failed, {summary.Skipped} skipped (Lucene index: '{indexPath}').");
    }

    internal static async ValueTask DisposeAsync(IServiceProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (provider is IDisposable disposable)
            disposable.Dispose();
    }
}
