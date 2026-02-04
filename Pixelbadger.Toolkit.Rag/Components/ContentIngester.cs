using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Components;

public class ContentIngester : IContentIngester
{
    private readonly ILuceneRepository _luceneRepo;
    private readonly IVectorRepository _vectorRepo;
    private readonly ChunkerFactory _chunkerFactory;
    private readonly FileReaderFactory _fileReaderFactory;

    public ContentIngester(
        ILuceneRepository luceneRepo,
        IVectorRepository vectorRepo,
        ChunkerFactory chunkerFactory,
        FileReaderFactory fileReaderFactory)
    {
        _luceneRepo = luceneRepo;
        _vectorRepo = vectorRepo;
        _chunkerFactory = chunkerFactory;
        _fileReaderFactory = fileReaderFactory;
    }

    public Task IngestContentAsync(string indexPath, string contentPath)
    {
        return IngestContentAsync(indexPath, contentPath, null);
    }

    public async Task IngestContentAsync(string indexPath, string contentPath, IngestOptions? options)
    {
        if (!File.Exists(contentPath))
        {
            throw new FileNotFoundException($"Content file not found: {contentPath}");
        }

        options ??= new IngestOptions();

        var content = await File.ReadAllTextAsync(contentPath);
        var chunks = await GetChunksForFileAsync(contentPath, content);

        // Filter out empty chunks
        var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();

        // Lucene BM25 indexing
        await _luceneRepo.IndexWithLuceneAsync(indexPath, contentPath, nonEmptyChunks);

        // Vector storage (always enabled for eval harness)
        await _vectorRepo.StoreVectorsAsync(indexPath, contentPath, nonEmptyChunks);
    }

    public async Task IngestFolderAsync(string indexPath, string folderPath, IngestOptions? options = null)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        options ??= new IngestOptions();

        // Discover all files in the folder
        var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

        // Filter to only supported file types
        var supportedFiles = allFiles.Where(file => _fileReaderFactory.CanRead(file)).ToList();

        if (supportedFiles.Count == 0)
        {
            Console.WriteLine($"No supported files found in {folderPath}");
            Console.WriteLine($"Supported extensions: {string.Join(", ", _fileReaderFactory.SupportedExtensions)}");
            return;
        }

        Console.WriteLine($"Found {supportedFiles.Count} supported files to ingest");

        // Process each file
        foreach (var filePath in supportedFiles)
        {
            try
            {
                Console.WriteLine($"Ingesting: {Path.GetFileName(filePath)}");

                // Get the appropriate reader for this file
                var reader = _fileReaderFactory.GetReader(filePath);

                // Read the content using the file reader
                var content = await reader.ReadTextAsync(filePath);

                // Chunk the content
                var chunks = await GetChunksForFileAsync(filePath, content);

                // Filter out empty chunks
                var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();

                if (nonEmptyChunks.Count == 0)
                {
                    Console.WriteLine($"  Skipped (no content): {Path.GetFileName(filePath)}");
                    continue;
                }

                // Lucene BM25 indexing
                await _luceneRepo.IndexWithLuceneAsync(indexPath, filePath, nonEmptyChunks);

                // Vector storage
                await _vectorRepo.StoreVectorsAsync(indexPath, filePath, nonEmptyChunks);

                Console.WriteLine($"  Indexed {nonEmptyChunks.Count} chunks from {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error ingesting {Path.GetFileName(filePath)}: {ex.Message}");
                // Continue processing other files
            }
        }

        Console.WriteLine($"Completed ingestion of {supportedFiles.Count} files");
    }

    private async Task<List<IChunk>> GetChunksForFileAsync(string filePath, string content)
    {
        var chunker = _chunkerFactory.GetChunker(filePath);
        return await chunker.ChunkTextAsync(content);
    }
}
