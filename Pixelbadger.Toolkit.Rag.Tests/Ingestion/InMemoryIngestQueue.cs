using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>
/// In-memory <see cref="IIngestQueue"/> with the same claim/lease/attempt semantics as <see cref="SqlIngestQueue"/>,
/// so the worker's behaviour can be tested without SQL Server.
/// </summary>
public sealed class InMemoryIngestQueue(IngestSettings settings) : IIngestQueue
{
    private sealed class File
    {
        public int Id;
        public int Ordinal;
        public string Path = "";
        public byte[]? Content;
        public IngestFileStatus Status;
        public string? DocumentId;
        public Modality? Modality;
        public int? ChunkCount;
        public string? Error;
    }

    private sealed class Job
    {
        public Guid Id = Guid.CreateVersion7();
        public IngestJobStatus Status;
        public int Attempts;
        public int MaxChunk;
        public DateTime Created = DateTime.UtcNow;
        public DateTime? Started;
        public DateTime? Completed;
        public DateTime? LeaseExpires;
        public string? LeaseOwner;
        public string? Error;
        public List<File> Files = new();
    }

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private int _nextFileId = 1;

    public async Task<Guid> EnqueueAsync(IReadOnlyList<IngestUpload> files, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        var job = new Job { Status = IngestJobStatus.Queued, MaxChunk = maxChunkCharacters };
        foreach (var upload in files)
        {
            using var buffer = new MemoryStream();
            await using (var source = upload.OpenRead())
                await source.CopyToAsync(buffer, cancellationToken);
            lock (_gate)
                job.Files.Add(new File { Id = _nextFileId++, Ordinal = job.Files.Count, Path = upload.LogicalPath, Content = buffer.ToArray() });
        }

        lock (_gate) _jobs.Add(job);
        return job.Id;
    }

    public Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var dead in _jobs.Where(j => j.Status == IngestJobStatus.Processing && j.LeaseExpires < now && j.Attempts >= settings.MaxAttempts))
                Fail(dead, dead.Error ?? "abandoned");

            var next = _jobs.OrderBy(j => j.Created)
                .FirstOrDefault(j => j.Status == IngestJobStatus.Queued
                    || (j.Status == IngestJobStatus.Processing && j.LeaseExpires < now && j.Attempts < settings.MaxAttempts));
            if (next is null)
                return Task.FromResult<IngestJobClaim?>(null);

            next.Status = IngestJobStatus.Processing;
            next.Attempts++;
            next.LeaseOwner = owner;
            next.LeaseExpires = now + lease;
            next.Started ??= now;
            next.Error = null;
            return Task.FromResult<IngestJobClaim?>(new IngestJobClaim(next.Id, next.Attempts, next.MaxChunk));
        }
    }

    public Task<IReadOnlyList<PendingIngestFile>> GetPendingFilesAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PendingIngestFile>>(Find(jobId).Files
                .Where(f => f.Status == IngestFileStatus.Queued).OrderBy(f => f.Ordinal)
                .Select(f => new PendingIngestFile(f.Id, f.Ordinal, f.Path, f.Content!.Length)).ToList());
        }
    }

    public async Task ReadFileContentAsync(int fileId, Stream destination, CancellationToken cancellationToken = default)
    {
        byte[] content;
        lock (_gate)
            content = _jobs.SelectMany(j => j.Files).Single(f => f.Id == fileId).Content
                ?? throw new InvalidOperationException("content cleared");
        await destination.WriteAsync(content, cancellationToken);
    }

    public Task CompleteFileAsync(int fileId, IngestFileOutcome outcome, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var file = _jobs.SelectMany(j => j.Files).Single(f => f.Id == fileId);
            file.Status = outcome.Status;
            file.DocumentId = outcome.DocumentGlobalId;
            file.Modality = outcome.Modality;
            file.ChunkCount = outcome.ChunkCount;
            file.Error = outcome.Error;
            file.Content = null;
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExtendLeaseAsync(Guid jobId, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            if (job.Status != IngestJobStatus.Processing || job.LeaseOwner != owner)
                return Task.FromResult(false);
            job.LeaseExpires = DateTime.UtcNow + lease;
            return Task.FromResult(true);
        }
    }

    public Task CompleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            job.Status = IngestJobStatus.Completed;
            job.Completed = DateTime.UtcNow;
            job.LeaseOwner = null;
            job.LeaseExpires = null;
        }

        return Task.CompletedTask;
    }

    public Task<IngestJobStatus> FailJobAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            if (job.Attempts >= settings.MaxAttempts)
            {
                Fail(job, error);
            }
            else
            {
                job.Status = IngestJobStatus.Queued;
                job.Error = error;
                job.LeaseOwner = null;
                job.LeaseExpires = null;
            }

            return Task.FromResult(job.Status);
        }
    }

    public Task<IngestJobStatusDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = _jobs.SingleOrDefault(j => j.Id == jobId);
            if (job is null)
                return Task.FromResult<IngestJobStatusDto?>(null);

            var files = job.Files.OrderBy(f => f.Ordinal)
                .Select(f => new IngestFileStatusDto(f.Path, f.Status, f.DocumentId, f.Modality, f.ChunkCount, f.Error)).ToList();
            return Task.FromResult<IngestJobStatusDto?>(new IngestJobStatusDto(
                job.Id, job.Status, job.Attempts, job.Created, job.Started, job.Completed, job.Error, files,
                new IngestJobSummaryDto(
                    files.Count(f => f.Status == IngestFileStatus.Succeeded),
                    files.Count(f => f.Status == IngestFileStatus.Failed),
                    files.Count(f => f.Status == IngestFileStatus.Skipped))));
        }
    }

    /// <summary>True when every file of the job has had its bytes discarded.</summary>
    public bool AllContentCleared(Guid jobId)
    {
        lock (_gate) return Find(jobId).Files.All(f => f.Content is null);
    }

    private Job Find(Guid jobId) => _jobs.Single(j => j.Id == jobId);

    private static void Fail(Job job, string error)
    {
        job.Status = IngestJobStatus.Failed;
        job.Completed = DateTime.UtcNow;
        job.Error = error;
        job.LeaseOwner = null;
        job.LeaseExpires = null;
        foreach (var file in job.Files.Where(f => f.Status == IngestFileStatus.Queued))
        {
            file.Status = IngestFileStatus.Failed;
            file.Error = "The job failed before this file was processed.";
            file.Content = null;
        }
    }
}
