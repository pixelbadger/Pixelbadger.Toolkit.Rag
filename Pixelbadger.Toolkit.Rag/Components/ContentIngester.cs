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
        options ??= new IngestOptions();

        if (!File.Exists(contentPath))
        {
            throw new FileNotFoundException($"Content file not found: {contentPath}");
        }

        ValidateIngestOptions(options);
        ValidateFileForIngestion(contentPath, options);

        var fileReader = _fileReaderFactory.GetReader(contentPath);
        var content = await fileReader.ReadTextAsync(contentPath);
        var chunks = await GetChunksForFileAsync(contentPath, content);

        // Filter out empty chunks
        var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();
        ValidateChunksForIngestion(contentPath, nonEmptyChunks, options);

        // Lucene BM25 indexing
        await _luceneRepo.IndexWithLuceneAsync(indexPath, contentPath, nonEmptyChunks);

        // Vector storage
        if (options.EnableVectorStorage)
        {
            await _vectorRepo.StoreVectorsAsync(indexPath, contentPath, nonEmptyChunks);
        }
    }

    public async Task IngestFolderAsync(string indexPath, string folderPath, IngestOptions? options = null)
    {
        options ??= new IngestOptions();

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        ValidateIngestOptions(options);

        if (!options.AllowSymlinks && IsReparsePoint(folderPath))
        {
            throw new InvalidOperationException($"Refusing to ingest symbolic link or reparse point folder: {folderPath}");
        }

        var rootPath = NormalizeRootPath(Path.GetFullPath(folderPath));

        // Discover files without following symlinked directories by default.
        var allFiles = EnumerateFilesSafely(rootPath, options).ToList();

        // Filter to only supported file types
        var supportedFiles = allFiles.Where(file => _fileReaderFactory.CanRead(file)).ToList();
        if (supportedFiles.Count > options.MaxFiles)
        {
            throw new InvalidOperationException($"Folder contains {supportedFiles.Count} supported files, exceeding the limit of {options.MaxFiles}");
        }

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
                ValidateFileWithinRoot(filePath, rootPath);
                ValidateFileForIngestion(filePath, options);

                // Get the appropriate reader for this file
                var reader = _fileReaderFactory.GetReader(filePath);

                // Read the content using the file reader
                var content = await reader.ReadTextAsync(filePath);

                // Chunk the content
                var chunks = await GetChunksForFileAsync(filePath, content);

                // Filter out empty chunks
                var nonEmptyChunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();
                ValidateChunksForIngestion(filePath, nonEmptyChunks, options);

                if (nonEmptyChunks.Count == 0)
                {
                    Console.WriteLine($"  Skipped (no content): {Path.GetFileName(filePath)}");
                    continue;
                }

                // Lucene BM25 indexing
                await _luceneRepo.IndexWithLuceneAsync(indexPath, filePath, nonEmptyChunks);

                // Vector storage
                if (options.EnableVectorStorage)
                {
                    await _vectorRepo.StoreVectorsAsync(indexPath, filePath, nonEmptyChunks);
                }

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

    private static IEnumerable<string> EnumerateFilesSafely(string rootPath, IngestOptions options)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (!options.AllowSymlinks && IsReparsePoint(directory))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                if (!options.AllowSymlinks && IsReparsePoint(file))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static void ValidateIngestOptions(IngestOptions options)
    {
        if (options.MaxFileSizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFileSizeBytes must be greater than zero");
        }

        if (options.MaxFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFiles must be greater than zero");
        }

        if (options.MaxChunkCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxChunkCharacters must be greater than zero");
        }
    }

    private static void ValidateFileForIngestion(string filePath, IngestOptions options)
    {
        if (!options.AllowSymlinks && IsReparsePoint(filePath))
        {
            throw new InvalidOperationException($"Refusing to ingest symbolic link or reparse point file: {filePath}");
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > options.MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File '{filePath}' is {fileInfo.Length} bytes, exceeding the limit of {options.MaxFileSizeBytes} bytes");
        }
    }

    private static void ValidateChunksForIngestion(string filePath, List<IChunk> chunks, IngestOptions options)
    {
        var oversizedChunk = chunks.FirstOrDefault(chunk => chunk.Content.Length > options.MaxChunkCharacters);
        if (oversizedChunk != null)
        {
            throw new InvalidOperationException($"File '{filePath}' produced a chunk with {oversizedChunk.Content.Length} characters, exceeding the limit of {options.MaxChunkCharacters} characters");
        }
    }

    private static void ValidateFileWithinRoot(string filePath, string rootPath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to ingest file outside folder root: {filePath}");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizeRootPath(string rootPath)
    {
        return rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
    }
}
