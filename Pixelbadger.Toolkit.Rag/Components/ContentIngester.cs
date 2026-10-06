using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Ingestion pipeline. Text: reader, chunker, embed, SQL, Lucene. Image: one chunk. Audio: one chunk
/// per window. Only text chunks go into the Lucene BM25 index.
/// </summary>
public class ContentIngester : IContentIngester
{
    private readonly RagOptions _options;
    private readonly IDocumentStore _store;
    private readonly ILuceneRepository _lucene;
    private readonly IndexWriteGate _gate;
    private readonly IEmbeddingService _embeddings;
    private readonly ChunkerFactory _chunkerFactory;
    private readonly FileReaderFactory _fileReaderFactory;
    private readonly IImagePreprocessor _imagePreprocessor;
    private readonly IAudioPreprocessor _audioPreprocessor;
    private readonly ILogger<ContentIngester> _logger;

    public ContentIngester(
        RagOptions options,
        IDocumentStore store,
        ILuceneRepository lucene,
        IndexWriteGate gate,
        IEmbeddingService embeddings,
        ChunkerFactory chunkerFactory,
        FileReaderFactory fileReaderFactory,
        IImagePreprocessor imagePreprocessor,
        IAudioPreprocessor audioPreprocessor,
        ILogger<ContentIngester> logger)
    {
        _options = options;
        _store = store;
        _lucene = lucene;
        _gate = gate;
        _embeddings = embeddings;
        _chunkerFactory = chunkerFactory;
        _fileReaderFactory = fileReaderFactory;
        _imagePreprocessor = imagePreprocessor;
        _audioPreprocessor = audioPreprocessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestResult> IngestAsync(IngestSource source, IngestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new IngestOptions();

        if (!File.Exists(source.LocalPath))
        {
            throw new FileNotFoundException($"Content file not found: {source.LocalPath}");
        }

        ValidateIngestOptions(options);
        ValidateFileForIngestion(source, options);

        var result = await IngestCoreAsync(source, options, cancellationToken);
        _logger.LogDebug("Ingested {LogicalPath}: {ChunkCount} {Modality} chunk(s)", result.FilePath, result.ChunkCount, result.Modality);
        return result;
    }

    private Modality? GetModality(string filePath)
    {
        var modality = MediaTypes.GetModality(filePath);
        return modality == Modality.Text && !_fileReaderFactory.CanRead(filePath) ? null : modality;
    }

    private async Task<IngestResult> IngestCoreAsync(IngestSource source, IngestOptions options, CancellationToken cancellationToken)
    {
        var logicalPath = LogicalPath.Normalize(source.LogicalPath);
        var modality = GetModality(logicalPath)
            ?? throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(logicalPath)}");

        var fileName = Path.GetFileName(logicalPath);
        var documentId = source.DocumentId;

        var draft = new DocumentDraft(
            logicalPath,
            fileName,
            modality,
            await ComputeContentHashAsync(source.LocalPath, cancellationToken));

        // Embedding (the slow part) happens outside the gate; only the SQL + Lucene writes are serialised with deletes.
        var chunks = modality switch
        {
            Modality.Text => await BuildTextChunksAsync(source.LocalPath, fileName, logicalPath, options, cancellationToken),
            Modality.Image => await BuildImageChunksAsync(source.LocalPath, cancellationToken),
            _ => await BuildAudioChunksAsync(source.LocalPath, cancellationToken)
        };

        using (await _gate.EnterAsync(cancellationToken))
        {
            // Throws DocumentNotFoundException when the document was deleted meanwhile (nothing is written).
            var records = await _store.ReplaceDocumentAsync(documentId, draft, chunks, cancellationToken);

            try
            {
                // Image/audio chunks are vector-only: BM25 indexes text chunks.
                var luceneChunks = records
                    .Where(r => r.Modality == Modality.Text && !string.IsNullOrWhiteSpace(r.Text))
                    .Select(r => new LuceneChunk(r.ChunkId, r.Text!))
                    .ToList();

                if (luceneChunks.Count > 0 || modality == Modality.Text)
                {
                    // Always called for text so stale entries of a previous version are removed.
                    await _lucene.ReplaceDocumentAsync(_options.IndexPath, documentId, luceneChunks, cancellationToken);
                }
            }
            catch
            {
                await TrySetFailedAsync(documentId);
                throw;
            }

            return new IngestResult(logicalPath, documentId, modality, records.Count);
        }
    }

    private async Task TrySetFailedAsync(Guid documentId)
    {
        try
        {
            await _store.SetIndexStatusAsync(documentId, IndexStatus.Failed, CancellationToken.None);
        }
        catch
        {
            // Best effort: the original Lucene failure is the error worth reporting.
        }
    }

    private async Task<List<ChunkDraft>> BuildTextChunksAsync(string filePath, string title, string logicalPath, IngestOptions options, CancellationToken cancellationToken)
    {
        var reader = _fileReaderFactory.GetReader(filePath);
        var content = await reader.ReadTextAsync(filePath);
        var chunks = await _chunkerFactory.GetChunker(filePath).ChunkTextAsync(content);

        // Filter out empty chunks
        var nonEmpty = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();
        ValidateChunksForIngestion(logicalPath, nonEmpty, options);

        if (nonEmpty.Count == 0)
        {
            return new List<ChunkDraft>();
        }

        var texts = nonEmpty.Select(c => c.Content).ToList();
        var locators = TextChunkLocator.Locate(content, texts);
        var embeddings = await _embeddings.EmbedDocumentTextAsync(title, texts, cancellationToken);
        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException($"Embedding service returned {embeddings.Count} vectors for {texts.Count} chunks of '{logicalPath}'");
        }

        var drafts = new List<ChunkDraft>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            drafts.Add(new ChunkDraft(i + 1, Modality.Text, locators[i]?.Start, locators[i]?.End, texts[i], embeddings[i]));
        }

        return drafts;
    }

    private async Task<List<ChunkDraft>> BuildImageChunksAsync(string filePath, CancellationToken cancellationToken)
    {
        var image = await _imagePreprocessor.PreprocessAsync(filePath, cancellationToken);
        var embedding = await _embeddings.EmbedImageAsync(image, cancellationToken);
        return new List<ChunkDraft> { new(1, Modality.Image, null, null, null, embedding) };
    }

    private async Task<List<ChunkDraft>> BuildAudioChunksAsync(string filePath, CancellationToken cancellationToken)
    {
        var windows = await _audioPreprocessor.PreprocessAsync(filePath, cancellationToken);
        var drafts = new List<ChunkDraft>(windows.Count);
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var embedding = await _embeddings.EmbedAudioAsync(window.Features, cancellationToken);
            drafts.Add(new ChunkDraft(i + 1, Modality.Audio, window.StartMs, window.EndMs, null, embedding));
        }

        return drafts;
    }

    private static async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void ValidateIngestOptions(IngestOptions options)
    {
        if (options.MaxFileSizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFileSizeBytes must be greater than zero");
        }

        if (options.MaxChunkCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxChunkCharacters must be greater than zero");
        }
    }

    private static void ValidateFileForIngestion(IngestSource source, IngestOptions options)
    {
        var fileInfo = new FileInfo(source.LocalPath);
        if (fileInfo.Length > options.MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File '{source.LogicalPath}' is {fileInfo.Length} bytes, exceeding the limit of {options.MaxFileSizeBytes} bytes");
        }
    }

    private static void ValidateChunksForIngestion(string logicalPath, List<IChunk> chunks, IngestOptions options)
    {
        var oversizedChunk = chunks.FirstOrDefault(chunk => chunk.Content.Length > options.MaxChunkCharacters);
        if (oversizedChunk != null)
        {
            throw new InvalidOperationException($"File '{logicalPath}' produced a chunk with {oversizedChunk.Content.Length} characters, exceeding the limit of {options.MaxChunkCharacters} characters");
        }
    }
}

/// <summary>
/// Locates chunk texts within the original file content as char offsets. Chunkers trim chunks and
/// may re-join lines with a different newline, so matching is done on newline-normalised text and
/// mapped back to offsets in the original string.
/// </summary>
public static class TextChunkLocator
{
    /// <summary>
    /// Returns, for each chunk, its [Start, End) char offsets in <paramref name="content"/>, or null if the
    /// chunk could not be found. Chunks are expected in document order; the search resumes after the
    /// previous match so repeated chunk texts map to successive occurrences.
    /// </summary>
    public static IReadOnlyList<(long Start, long End)?> Locate(string content, IReadOnlyList<string> chunks)
    {
        // Normalise \r\n and lone \r to \n, remembering each normalised char's original offset.
        var normalised = new StringBuilder(content.Length);
        var map = new List<int>(content.Length);
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\r')
            {
                normalised.Append('\n');
                map.Add(i);
                if (i + 1 < content.Length && content[i + 1] == '\n') i++;
            }
            else
            {
                normalised.Append(c);
                map.Add(i);
            }
        }

        var text = normalised.ToString();
        var result = new (long Start, long End)?[chunks.Count];
        var cursor = 0;

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = NormaliseNewlines(chunks[i]);
            if (chunk.Length == 0) continue;

            var index = text.IndexOf(chunk, cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                index = text.IndexOf(chunk, StringComparison.Ordinal);
            }

            if (index < 0) continue;

            var lastNormalised = index + chunk.Length - 1;
            var start = map[index];
            // Chunks end on a non-newline char, so the original end is one past that char.
            var end = map[lastNormalised] + 1;
            result[i] = (start, end);
            cursor = index + chunk.Length;
        }

        return result;
    }

    private static string NormaliseNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
}
