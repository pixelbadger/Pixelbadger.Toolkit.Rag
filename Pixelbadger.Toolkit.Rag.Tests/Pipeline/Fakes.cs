using Microsoft.Extensions.Logging.Abstractions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>In-memory IDocumentStore with brute-force cosine search, for unit tests.</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private sealed record Row(ChunkRecord Record, float[] Embedding);

    private readonly object _gate = new();
    private readonly Dictionary<string, (int Id, DocumentDraft Draft, IndexStatus Status)> _documents = new();
    private readonly List<Row> _rows = new();
    private int _nextDocumentId = 1;
    private int _nextChunkId = 1;

    public int EnsureVectorIndexCalls { get; private set; }
    public int ReplaceCalls { get; private set; }

    public IReadOnlyList<ChunkRecord> AllChunks
    {
        get { lock (_gate) return _rows.Select(r => r.Record).ToList(); }
    }

    public IndexStatus? StatusOf(string documentGlobalId)
    {
        lock (_gate) return _documents.TryGetValue(documentGlobalId, out var d) ? d.Status : null;
    }

    public DocumentDraft? DraftOf(string documentGlobalId)
    {
        lock (_gate) return _documents.TryGetValue(documentGlobalId, out var d) ? d.Draft : null;
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ChunkRecord>> ReplaceDocumentAsync(DocumentDraft document, IReadOnlyList<ChunkDraft> chunks, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReplaceCalls++;
            if (!_documents.TryGetValue(document.GlobalId, out var existing))
            {
                existing = (_nextDocumentId++, document, IndexStatus.Indexed);
            }

            _documents[document.GlobalId] = (existing.Id, document, IndexStatus.Indexed);
            _rows.RemoveAll(r => r.Record.DocumentId == existing.Id);

            var records = new List<ChunkRecord>();
            foreach (var chunk in chunks.OrderBy(c => c.Ordinal))
            {
                var record = new ChunkRecord(
                    _nextChunkId++, Guid.NewGuid(), existing.Id, document.GlobalId, document.SourcePath, document.SourceId,
                    document.Title, chunk.Ordinal, chunk.Modality, chunk.LocatorStart, chunk.LocatorEnd, chunk.Text);
                _rows.Add(new Row(record, chunk.Embedding));
                records.Add(record);
            }

            return Task.FromResult<IReadOnlyList<ChunkRecord>>(records);
        }
    }

    public Task SetIndexStatusAsync(string documentGlobalId, IndexStatus status, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(documentGlobalId, out var d))
            {
                _documents[documentGlobalId] = (d.Id, d.Draft, status);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryEmbedding, int maxResults, IReadOnlyCollection<string>? sourceIds, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var hits = _rows
                .Where(r => sourceIds == null || sourceIds.Count == 0 || sourceIds.Contains(r.Record.SourceId))
                .Select(r => new VectorHit(r.Record.ChunkId, 1f - Dot(queryEmbedding, r.Embedding)))
                .OrderBy(h => h.Distance)
                .Take(maxResults)
                .ToList();
            return Task.FromResult<IReadOnlyList<VectorHit>>(hits);
        }
    }

    public Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyCollection<int> chunkIds, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var wanted = chunkIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<ChunkRecord>>(_rows.Where(r => wanted.Contains(r.Record.ChunkId)).Select(r => r.Record).ToList());
        }
    }

    public Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) EnsureVectorIndexCalls++;
        return Task.CompletedTask;
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

public sealed class FakeImagePreprocessor : IImagePreprocessor
{
    public Task<PreprocessedImage> PreprocessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Content-dependent pixels so different files embed differently.
        var bytes = File.ReadAllBytes(filePath);
        var pixels = new float[768];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = bytes.Length == 0 ? 0 : bytes[i % bytes.Length] / 255f;
        return Task.FromResult(new PreprocessedImage(pixels, new long[2], 1, 48, 48));
    }
}

public sealed class FakeAudioPreprocessor : IAudioPreprocessor
{
    public int WindowCount { get; set; } = 2;

    public Task<IReadOnlyList<AudioWindow>> PreprocessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var windows = new List<AudioWindow>();
        for (int i = 0; i < WindowCount; i++)
        {
            var features = new float[PreprocessedAudio.MelBins * 4];
            Array.Fill(features, i + 1f);
            windows.Add(new AudioWindow(i * 30_000L, (i + 1) * 30_000L, new PreprocessedAudio(features, new bool[4], 1, 4)));
        }

        return Task.FromResult<IReadOnlyList<AudioWindow>>(windows);
    }
}

/// <summary>Lucene repository whose writes always fail, to exercise IndexStatus.Failed.</summary>
public sealed class FailingLuceneRepository : ILuceneRepository
{
    public Task ReplaceDocumentAsync(string indexPath, string documentGlobalId, string sourceId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default)
        => throw new IOException("simulated Lucene failure");

    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<string>? sourceIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<KeywordHit>>(Array.Empty<KeywordHit>());
}

/// <summary>Builds the full pipeline graph over a temp directory and a given store.</summary>
public sealed class PipelineHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pbrag_pipeline_" + Guid.NewGuid().ToString("N"));
    public string ContentDir => Path.Combine(Root, "content");
    public string IndexPath => Path.Combine(Root, "index");
    public RagOptions Options { get; }
    public IDocumentStore Store { get; }
    public ContentIngester Ingester { get; }
    public SearchService Search { get; }
    public FakeAudioPreprocessor Audio { get; } = new();

    public PipelineHarness(IDocumentStore? store = null, ILuceneRepository? lucene = null)
    {
        Directory.CreateDirectory(ContentDir);
        Options = new RagOptions { IndexPath = IndexPath };
        Store = store ?? new InMemoryDocumentStore();
        lucene ??= new LuceneRepository();
        var embeddings = new Support.MockEmbeddingService();

        var chunkers = new ITextChunker[] { new MarkdownTextChunker(), new ParagraphTextChunker() };
        var readers = new Components.FileReaders.IFileReader[]
        {
            new Components.FileReaders.PlainTextFileReader(),
            new Components.FileReaders.MarkdownFileReader()
        };

        Ingester = new ContentIngester(
            Options, Store, lucene, embeddings,
            new ChunkerFactory(chunkers), new Components.FileReaders.FileReaderFactory(readers),
            new FakeImagePreprocessor(), Audio, NullLogger<ContentIngester>.Instance);
        Search = new SearchService(Options, lucene, Store, embeddings, new RrfReranker());
    }

    /// <summary>The logical path of a file under <see cref="ContentDir"/>: its relative path with '/' separators.</summary>
    public string LogicalPathOf(string localPath) => Path.GetRelativePath(ContentDir, localPath).Replace('\\', '/');

    /// <summary>Ingests a file under <see cref="ContentDir"/> using its relative path as the logical path.</summary>
    public Task<IngestResult> IngestAsync(string localPath, IngestOptions? options = null)
        => Ingester.IngestAsync(new IngestSource(localPath, LogicalPathOf(localPath)), options);

    /// <summary>Ingests every file under <see cref="ContentDir"/> (all must be supported).</summary>
    public async Task<IReadOnlyList<IngestResult>> IngestAllAsync()
    {
        var results = new List<IngestResult>();
        foreach (var path in Directory.EnumerateFiles(ContentDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            results.Add(await IngestAsync(path));
        return results;
    }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(ContentDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(ContentDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}
