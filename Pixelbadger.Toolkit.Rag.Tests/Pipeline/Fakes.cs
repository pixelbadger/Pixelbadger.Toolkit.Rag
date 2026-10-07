using Microsoft.Extensions.Logging.Abstractions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>In-memory IDocumentStore with brute-force cosine search, for unit tests.</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private sealed record Row(ChunkRecord Record, float[] Embedding);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (int Id, DocumentDraft Draft, IndexStatus Status)> _documents = new();
    private readonly List<Row> _rows = new();
    private int _nextDocumentId = 1;
    private int _nextChunkId = 1;

    public int EnsureVectorIndexCalls { get; private set; }
    public int ReplaceCalls { get; private set; }

    public IReadOnlyList<ChunkRecord> AllChunks
    {
        get { lock (_gate) return _rows.Select(r => r.Record).ToList(); }
    }

    /// <summary>What the upload endpoint does in production: the document exists (Queued) before any ingest runs.</summary>
    public Guid CreateDocument(string logicalPath)
    {
        lock (_gate)
        {
            var id = Guid.CreateVersion7();
            var draft = new DocumentDraft(logicalPath, Path.GetFileName(logicalPath), MediaTypes.GetModality(logicalPath) ?? Modality.Text, string.Empty);
            _documents[id] = (_nextDocumentId++, draft, IndexStatus.Queued);
            return id;
        }
    }

    public IReadOnlyList<Guid> AllDocumentIds
    {
        get { lock (_gate) return _documents.Keys.ToList(); }
    }

    public bool Exists(Guid documentId)
    {
        lock (_gate) return _documents.ContainsKey(documentId);
    }

    public IndexStatus? StatusOf(Guid documentId)
    {
        lock (_gate) return _documents.TryGetValue(documentId, out var d) ? d.Status : null;
    }

    public DocumentDraft? DraftOf(Guid documentId)
    {
        lock (_gate) return _documents.TryGetValue(documentId, out var d) ? d.Draft : null;
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ChunkRecord>> ReplaceDocumentAsync(Guid documentId, DocumentDraft document, IReadOnlyList<ChunkDraft> chunks, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReplaceCalls++;
            if (!_documents.TryGetValue(documentId, out var existing))
                throw new DocumentNotFoundException(documentId);

            // Like the SQL store: a draft without bytes leaves the stored source alone.
            _documents[documentId] = (existing.Id, document.SourceContent is null ? document with { SourceContent = existing.Draft.SourceContent, ContentType = existing.Draft.ContentType } : document, IndexStatus.Indexed);
            _rows.RemoveAll(r => r.Record.DocumentId == existing.Id);

            var records = new List<ChunkRecord>();
            foreach (var chunk in chunks.OrderBy(c => c.Ordinal))
            {
                var record = new ChunkRecord(
                    _nextChunkId++, Guid.NewGuid(), existing.Id, documentId, document.SourcePath,
                    document.Title, chunk.Ordinal, chunk.Modality, chunk.LocatorStart, chunk.LocatorEnd, chunk.Text);
                _rows.Add(new Row(record, chunk.Embedding));
                records.Add(record);
            }

            return Task.FromResult<IReadOnlyList<ChunkRecord>>(records);
        }
    }

    public Task<DocumentContent?> GetContentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(documentId, out var d) || d.Draft.SourceContent is null)
                return Task.FromResult<DocumentContent?>(null);
            return Task.FromResult<DocumentContent?>(new DocumentContent(
                d.Draft.SourceContent, d.Draft.ContentType ?? "application/octet-stream", Path.GetFileName(d.Draft.SourcePath)));
        }
    }

    public Task<bool> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_documents.Remove(documentId, out var removed))
                return Task.FromResult(false);

            _rows.RemoveAll(r => r.Record.DocumentId == removed.Id);
            return Task.FromResult(true);
        }
    }

    public Task SetIndexStatusAsync(Guid documentId, IndexStatus status, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(documentId, out var d))
            {
                _documents[documentId] = (d.Id, d.Draft, status);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryEmbedding, int maxResults, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var hits = _rows
                .Where(r => documentIds == null || documentIds.Count == 0 || documentIds.Contains(r.Record.DocumentGlobalId))
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
    public Task ReplaceDocumentAsync(string indexPath, Guid documentId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default)
        => throw new IOException("simulated Lucene failure");

    public Task DeleteDocumentAsync(string indexPath, Guid documentId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<KeywordHit>>(Array.Empty<KeywordHit>());
}

/// <summary>
/// Wraps a Lucene repository and pauses inside <see cref="ReplaceDocumentAsync"/> (after the SQL write, before the
/// Lucene write) until released, so a test can land a delete in exactly that window.
/// </summary>
public sealed class PausingLuceneRepository(ILuceneRepository inner) : ILuceneRepository
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task ReplaceDocumentAsync(string indexPath, Guid documentId, IReadOnlyList<LuceneChunk> chunks, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _release.Task;
        await inner.ReplaceDocumentAsync(indexPath, documentId, chunks, cancellationToken);
    }

    public Task DeleteDocumentAsync(string indexPath, Guid documentId, CancellationToken cancellationToken = default)
        => inner.DeleteDocumentAsync(indexPath, documentId, cancellationToken);

    public Task<IReadOnlyList<KeywordHit>> SearchAsync(string indexPath, string queryText, int maxResults, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
        => inner.SearchAsync(indexPath, queryText, maxResults, documentIds, cancellationToken);
}

/// <summary>Builds the full pipeline graph over a temp directory and a given store.</summary>
public sealed class PipelineHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pbrag_pipeline_" + Guid.NewGuid().ToString("N"));
    public string ContentDir => Path.Combine(Root, "content");
    public string IndexPath => Path.Combine(Root, "index");
    public RagOptions Options { get; }
    public IDocumentStore Store { get; }
    public ILuceneRepository Lucene { get; }
    public IndexWriteGate Gate { get; } = new();
    public ContentIngester Ingester { get; }
    public SearchService Search { get; }
    public FakeAudioPreprocessor Audio { get; } = new();

    private readonly Func<string, Task<Guid>> _createDocument;

    /// <param name="createDocument">
    /// Creates the (Queued) document row an ingest writes into, from a logical path, as the upload endpoint does.
    /// Defaults to the in-memory store's own.
    /// </param>
    public PipelineHarness(IDocumentStore? store = null, ILuceneRepository? lucene = null, Func<string, Task<Guid>>? createDocument = null)
    {
        Directory.CreateDirectory(ContentDir);
        Options = new RagOptions { IndexPath = IndexPath };
        Store = store ?? new InMemoryDocumentStore();
        _createDocument = createDocument
            ?? (Store is InMemoryDocumentStore memory
                ? path => Task.FromResult(memory.CreateDocument(path))
                : throw new ArgumentException("A document factory is required for a store other than InMemoryDocumentStore.", nameof(createDocument)));
        lucene ??= new LuceneRepository();
        Lucene = lucene;
        var embeddings = new Support.MockEmbeddingService();

        var chunkers = new ITextChunker[] { new MarkdownTextChunker(), new ParagraphTextChunker() };
        var readers = new Components.FileReaders.IFileReader[]
        {
            new Components.FileReaders.PlainTextFileReader(),
            new Components.FileReaders.MarkdownFileReader()
        };

        Ingester = new ContentIngester(
            Options, Store, lucene, Gate, embeddings,
            new ChunkerFactory(chunkers), new Components.FileReaders.FileReaderFactory(readers),
            new FakeImagePreprocessor(), Audio, NullLogger<ContentIngester>.Instance);
        Search = new SearchService(Options, lucene, Store, embeddings, new RrfReranker());
    }

    /// <summary>The logical path of a file under <see cref="ContentDir"/>: its relative path with '/' separators.</summary>
    public string LogicalPathOf(string localPath) => Path.GetRelativePath(ContentDir, localPath).Replace('\\', '/');

    /// <summary>A <see cref="DocumentService"/> over this harness's store, Lucene index and gate.</summary>
    public DocumentService NewDocumentService() => new(
        Options, new IngestSettings(), Store, Lucene, new IngestJobRegistry(), Gate, NullLogger<DocumentService>.Instance);

    /// <summary>Creates a new document for a logical path (what a document upload does before its job runs).</summary>
    public Task<Guid> NewDocumentAsync(string logicalPath) => _createDocument(LogicalPath.Normalize(logicalPath));

    /// <summary>
    /// Ingests a file under <see cref="ContentDir"/> using its relative path as the logical path, into
    /// <paramref name="documentId"/> or, when null, into a new document.
    /// </summary>
    public async Task<IngestResult> IngestAsync(string localPath, IngestOptions? options = null, Guid? documentId = null)
    {
        var logicalPath = LogicalPathOf(localPath);
        documentId ??= await NewDocumentAsync(logicalPath);
        return await Ingester.IngestAsync(new IngestSource(localPath, logicalPath, documentId.Value), options);
    }

    /// <summary>Ingests every file under <see cref="ContentDir"/> (all must be supported), each as its own new document.</summary>
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
