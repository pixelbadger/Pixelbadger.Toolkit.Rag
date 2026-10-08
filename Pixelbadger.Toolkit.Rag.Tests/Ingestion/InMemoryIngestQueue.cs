using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>
/// In-memory <see cref="IIngestQueue"/> with the same document/job, begin-processing, attempt and re-ingest semantics
/// as <see cref="SqlIngestQueue"/>, and the same <see cref="JobStatusChanged"/> events (recorded in <see cref="Events"/>
/// exactly when the SQL implementation publishes them), so the ingest pipeline can be tested without SQL Server.
/// </summary>
public sealed class InMemoryIngestQueue : IIngestQueue
{
    private sealed class Doc
    {
        public Guid Id = Guid.CreateVersion7();
        public string Path = "";
        public Modality Modality;
        public IndexStatus Status;
        public DateTime Updated = DateTime.UtcNow;
        public List<Job> Jobs = new();
    }

    private sealed class Job
    {
        public Guid Id = Guid.CreateVersion7();
        public Doc Doc = null!;
        public IngestJobStatus Status;
        public int Attempts;
        public int MaxChunk;
        public string Path = "";
        public byte[]? Content;
        public int? ChunkCount;
        public DateTime Created = DateTime.UtcNow;
        public DateTime? Started;
        public DateTime? Completed;
        public string? Error;
    }

    private readonly object _gate = new();
    private readonly List<Doc> _docs = new();
    private readonly List<JobStatusChanged> _events = new();

    /// <summary>Fails the next <see cref="EnqueueNewDocumentsAsync"/> after storing this many uploads (atomicity tests).</summary>
    public int? FailEnqueueAfter { get; set; }

    /// <summary>The <see cref="JobStatusChanged"/> events published so far, in order (a snapshot).</summary>
    public IReadOnlyList<JobStatusChanged> Events
    {
        get
        {
            lock (_gate) return _events.ToList();
        }
    }

    public async Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(
        IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        var created = new List<(Doc Doc, Job Job)>();
        foreach (var upload in uploads)
        {
            if (FailEnqueueAfter == created.Count)
                throw new IOException("simulated enqueue failure");

            var content = await ReadAllAsync(upload, cancellationToken);
            var doc = new Doc { Path = upload.LogicalPath, Modality = MediaTypes.GetModality(upload.LogicalPath)!.Value, Status = IndexStatus.Queued };
            var job = new Job { Doc = doc, Status = IngestJobStatus.Queued, MaxChunk = maxChunkCharacters, Path = upload.LogicalPath, Content = content };
            doc.Jobs.Add(job);
            created.Add((doc, job));
        }

        // All or nothing, like the SQL transaction: documents and their Queued events appear together.
        lock (_gate)
        {
            _docs.AddRange(created.Select(c => c.Doc));
            foreach (var (_, job) in created)
                _events.Add(new JobStatusChanged(job.Id, IngestJobStatus.Queued));
        }

        return created.Select(c => new EnqueuedDocument(c.Doc.Id, c.Job.Id)).ToList();
    }

    public async Task<ReingestResult> EnqueueReingestAsync(
        Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        var content = await ReadAllAsync(upload, cancellationToken);
        lock (_gate)
        {
            var doc = _docs.SingleOrDefault(d => d.Id == documentId);
            if (doc is null)
                return new ReingestResult(ReingestOutcome.NotFound);

            var active = doc.Jobs.Where(j => !IngestJob.IsTerminal(j.Status)).OrderByDescending(j => j.Status).FirstOrDefault();
            if (active?.Status == IngestJobStatus.Processing)
                return new ReingestResult(ReingestOutcome.Conflict);

            ReingestOutcome outcome;
            if (active is not null)
            {
                // Replaced in place: the status is unchanged, so no event.
                active.Content = content;
                active.Path = upload.LogicalPath;
                active.MaxChunk = maxChunkCharacters;
                active.Attempts = 0;
                active.Error = null;
                outcome = ReingestOutcome.ReplacedQueued;
            }
            else
            {
                active = new Job { Doc = doc, Status = IngestJobStatus.Queued, MaxChunk = maxChunkCharacters, Path = upload.LogicalPath, Content = content };
                doc.Jobs.Add(active);
                _events.Add(new JobStatusChanged(active.Id, IngestJobStatus.Queued));
                outcome = ReingestOutcome.Created;
            }

            doc.Path = upload.LogicalPath;
            doc.Modality = MediaTypes.GetModality(upload.LogicalPath)!.Value;
            doc.Status = IndexStatus.Queued;
            doc.Updated = DateTime.UtcNow;
            return new ReingestResult(outcome, active.Id);
        }
    }

    public Task<IngestJobClaim?> BeginProcessingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = FindJob(jobId);
            if (job is null || IngestJob.IsTerminal(job.Status))
                return Task.FromResult<IngestJobClaim?>(null);

            job.Status = IngestJobStatus.Processing;
            job.Attempts++;
            job.Started ??= DateTime.UtcNow;
            job.Error = null;
            job.Doc.Status = IndexStatus.Processing;
            _events.Add(new JobStatusChanged(job.Id, IngestJobStatus.Processing));
            return Task.FromResult<IngestJobClaim?>(new IngestJobClaim(
                job.Id, job.Doc.Id, job.Attempts, job.MaxChunk, job.Path, job.Content!.Length));
        }
    }

    public async Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default)
    {
        byte[] content;
        lock (_gate)
            content = FindJob(jobId)?.Content ?? throw new InvalidOperationException("content not available");
        await destination.WriteAsync(content, cancellationToken);
    }

    public Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = FindJob(jobId);
            if (job is null)
                return Task.CompletedTask;

            job.Status = outcome.Status;
            job.ChunkCount = outcome.ChunkCount;
            job.Error = outcome.Error;
            job.Content = null;
            job.Completed = DateTime.UtcNow;
            job.Doc.Status = outcome.Status == IngestJobStatus.Failed ? IndexStatus.Failed : IndexStatus.Indexed;
            _events.Add(new JobStatusChanged(job.Id, outcome.Status));
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_docs.SelectMany(d => d.Jobs).Any(j => !IngestJob.IsTerminal(j.Status)));
    }

    public Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var doc = _docs.SingleOrDefault(d => d.Id == documentId);
            if (doc is null)
                return Task.FromResult<DocumentDto?>(null);

            var latest = doc.Jobs.OrderByDescending(j => j.Created).ThenByDescending(j => j.Id).FirstOrDefault();
            var chunks = doc.Jobs.Where(j => j.Status == IngestJobStatus.Succeeded).Select(j => j.ChunkCount ?? 0).LastOrDefault();
            return Task.FromResult<DocumentDto?>(new DocumentDto(
                doc.Id, doc.Path, Path.GetFileName(doc.Path), doc.Modality, doc.Status, chunks, doc.Updated,
                latest is null ? null : new IngestJobDto(latest.Id, latest.Status, latest.Attempts, latest.Created, latest.Started, latest.Completed, latest.Error)));
        }
    }

    public Task<IngestJobPage> GetJobsAsync(
        int page, int pageSize, IngestJobStatus? status = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var jobs = _docs.SelectMany(d => d.Jobs).Where(j => status is null || j.Status == status).ToList();
            var items = jobs.OrderByDescending(j => j.Created).ThenByDescending(j => j.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(j => new IngestJobListItemDto(
                    j.Id, j.Doc.Id, j.Path, j.Status, j.Attempts, j.MaxChunk, j.Content?.Length ?? 0, j.ChunkCount,
                    j.Created, j.Started, j.Completed, j.Error))
                .ToList();
            return Task.FromResult(new IngestJobPage(items, page, pageSize, jobs.Count));
        }
    }

    /// <summary>
    /// Simulates the SQL cascade of a document delete: the document and all its jobs vanish. No event is published
    /// (a delete is not a job status change). Returns false when the document is unknown.
    /// </summary>
    public bool RemoveDocument(Guid documentId)
    {
        lock (_gate) return _docs.RemoveAll(d => d.Id == documentId) > 0;
    }

    /// <summary>True when every job's bytes have been discarded.</summary>
    public bool AllContentCleared()
    {
        lock (_gate) return _docs.SelectMany(d => d.Jobs).All(j => j.Content is null);
    }

    private Job? FindJob(Guid jobId) => _docs.SelectMany(d => d.Jobs).SingleOrDefault(j => j.Id == jobId);

    private static async Task<byte[]> ReadAllAsync(IngestUpload upload, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using var source = upload.OpenRead();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
