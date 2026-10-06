using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>
/// Worker behaviour against a mocked <see cref="IContentIngester"/>. Subclasses choose the queue: in-memory (unit)
/// or the real SQL queue (integration).
/// </summary>
public abstract class IngestWorkerBehaviorTests
{
    protected abstract Task<IIngestQueue> CreateQueueAsync(IngestSettings settings);

    /// <summary>Deletes the document with its jobs, as the SQL cascade of a document delete does.</summary>
    protected abstract Task DeleteDocumentAsync(Guid documentId);

    protected sealed class Harness(
        IngestWorker worker, IIngestQueue queue, Mock<IContentIngester> ingester, Mock<IDocumentStore> store,
        IngestSettings settings, IngestJobRegistry registry)
    {
        public IngestWorker Worker { get; } = worker;
        public IIngestQueue Queue { get; } = queue;
        public Mock<IContentIngester> Ingester { get; } = ingester;
        public Mock<IDocumentStore> Store { get; } = store;
        public IngestSettings Settings { get; } = settings;
        public IngestJobRegistry Registry { get; } = registry;

        public async Task<EnqueuedDocument> EnqueueAsync(string path, string content, int maxChunk = 1000) =>
            (await EnqueueManyAsync(maxChunk, (path, content)))[0];

        public async Task<IReadOnlyList<EnqueuedDocument>> EnqueueManyAsync(int maxChunk, params (string Path, string Content)[] files) =>
            await Queue.EnqueueNewDocumentsAsync(
                files.Select(f => new IngestUpload(f.Path, f.Content.Length, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.Content)))).ToList(),
                maxChunk);

        public async Task<DocumentDto> DocumentAsync(Guid documentId) => (await Queue.GetDocumentAsync(documentId))!;
    }

    protected async Task<Harness> NewHarnessAsync(int maxAttempts = 3, int leaseSeconds = 600, Func<IIngestQueue, IIngestQueue>? decorate = null)
    {
        var settings = new IngestSettings { MaxAttempts = maxAttempts, LeaseSeconds = leaseSeconds, MaxFileSizeBytes = 1024 * 1024 };
        var queue = await CreateQueueAsync(settings);
        var ingester = new Mock<IContentIngester>();
        var store = new Mock<IDocumentStore>();
        var services = new ServiceCollection()
            .AddSingleton(decorate?.Invoke(queue) ?? queue)
            .AddSingleton(ingester.Object)
            .AddSingleton(store.Object)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var registry = new IngestJobRegistry();
        var worker = new IngestWorker(
            scopes, settings, new IngestWorkerSignal(), registry,
            new InFlightJobRecovery(scopes, NullLogger<InFlightJobRecovery>.Instance), NullLogger<IngestWorker>.Instance);
        return new Harness(worker, queue, ingester, store, settings, registry);
    }

    /// <summary>Succeeds with one chunk; records what the ingester saw on disk.</summary>
    private static void SucceedWithOneChunk(Harness h, List<(string Local, IngestSource Source, string Content, IngestOptions? Options)> seen)
    {
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? o, CancellationToken _) =>
            {
                seen.Add((s.LocalPath, s, File.ReadAllText(s.LocalPath), o));
                return new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, 1);
            });
    }

    private static string JobTempDirectory(Guid jobId) => Path.Combine(Path.GetTempPath(), "pbrag-ingest", jobId.ToString("N"));

    [SkippableFact]
    public async Task ProcessNext_ReturnsFalse_WhenQueueIsEmpty()
    {
        var h = await NewHarnessAsync();

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task ProcessNext_IngestsTheFile_WithOriginalExtensionLogicalPathAndDocumentId_ThenCompletes()
    {
        var h = await NewHarnessAsync();
        var seen = new List<(string Local, IngestSource Source, string Content, IngestOptions? Options)>();
        SucceedWithOneChunk(h, seen);
        var created = await h.EnqueueAsync("docs/a.MD", "# alpha", maxChunk: 500);

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        var call = seen.Single();
        call.Source.LogicalPath.Should().Be("docs/a.MD");
        call.Source.DocumentId.Should().Be(created.DocumentId);
        call.Content.Should().Be("# alpha");
        Path.GetExtension(call.Local).Should().Be(".MD");
        call.Options!.MaxChunkCharacters.Should().Be(500);
        call.Options.MaxFileSizeBytes.Should().Be(h.Settings.MaxFileSizeBytes);

        var document = await h.DocumentAsync(created.DocumentId);
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        document.LatestJob!.JobId.Should().Be(created.JobId);
        document.LatestJob.Status.Should().Be(IngestJobStatus.Succeeded);
        document.LatestJob.Attempts.Should().Be(1);
        document.LatestJob.StartedAtUtc.Should().NotBeNull();
        document.LatestJob.CompletedAtUtc.Should().NotBeNull();

        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
        File.Exists(call.Local).Should().BeFalse();
    }

    [SkippableFact]
    public async Task VectorIndex_IsBuiltOnceWhenIdle_NotAfterEveryJob()
    {
        var h = await NewHarnessAsync();
        SucceedWithOneChunk(h, []);
        await h.EnqueueAsync("one.txt", "1");
        await h.EnqueueAsync("two.txt", "2");

        await h.Worker.ProcessNextAsync(CancellationToken.None);
        await h.Worker.ProcessNextAsync(CancellationToken.None);
        h.Store.Verify(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()), Times.Never);

        await h.Worker.EnsureVectorIndexIfStaleAsync(CancellationToken.None);
        await h.Worker.EnsureVectorIndexIfStaleAsync(CancellationToken.None);

        h.Store.Verify(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [SkippableFact]
    public async Task ProcessNext_FileProblem_FailsTheJobWithoutRetry_AndEmptyContentIsSkipped()
    {
        var h = await NewHarnessAsync();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken _) =>
                s.LogicalPath == "bad.txt"
                    ? throw new InvalidOperationException("chunk too large")
                    : new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, s.LogicalPath == "empty.txt" ? 0 : 2));
        var created = await h.EnqueueManyAsync(1000, ("good.txt", "g"), ("bad.txt", "b"), ("empty.txt", "e"));

        for (var i = 0; i < 3; i++)
            (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("a file problem is not retried");

        var good = await h.DocumentAsync(created[0].DocumentId);
        good.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        good.IndexStatus.Should().Be(IndexStatus.Indexed);

        var bad = await h.DocumentAsync(created[1].DocumentId);
        bad.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        bad.LatestJob.Error.Should().Be("chunk too large");
        bad.LatestJob.Attempts.Should().Be(1);
        bad.IndexStatus.Should().Be(IndexStatus.Failed);

        var empty = await h.DocumentAsync(created[2].DocumentId);
        empty.LatestJob!.Status.Should().Be(IngestJobStatus.Skipped);
        empty.IndexStatus.Should().Be(IndexStatus.Indexed);
        Directory.Exists(JobTempDirectory(created[1].JobId)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task ProcessNext_Shutdown_LeavesJobProcessing_AndALaterClaimRetriesIt()
    {
        var h = await NewHarnessAsync(leaseSeconds: 0); // a lease that is already expired when the retry claims
        using var shutdown = new CancellationTokenSource();
        var calls = 0;
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken ct) =>
            {
                if (calls++ == 0)
                {
                    shutdown.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, 1);
            });
        var created = await h.EnqueueAsync("a.txt", "1");

        await h.Worker.ProcessNextAsync(shutdown.Token);

        var interrupted = await h.DocumentAsync(created.DocumentId);
        interrupted.LatestJob!.Status.Should().Be(IngestJobStatus.Processing);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();

        await Task.Delay(50); // let the zero-length lease lapse
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        var done = await h.DocumentAsync(created.DocumentId);
        done.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        done.LatestJob.Attempts.Should().Be(2);
    }

    [SkippableFact]
    public async Task ProcessNext_JobLevelFailure_RequeuesUntilAttemptsAreExhausted()
    {
        var h = await NewHarnessAsync(maxAttempts: 2, decorate: q => new CompleteFailingQueue(q));
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken _) => new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, 1));
        var created = await h.EnqueueAsync("a.txt", "a");

        await h.Worker.ProcessNextAsync(CancellationToken.None);
        var afterFirst = await h.DocumentAsync(created.DocumentId);
        afterFirst.LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        afterFirst.LatestJob.Attempts.Should().Be(1);
        afterFirst.LatestJob.Error.Should().Be("queue storage offline");
        afterFirst.IndexStatus.Should().Be(IndexStatus.Queued);

        await h.Worker.ProcessNextAsync(CancellationToken.None);
        var afterSecond = await h.DocumentAsync(created.DocumentId);
        afterSecond.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        afterSecond.LatestJob.Attempts.Should().Be(2);
        afterSecond.LatestJob.CompletedAtUtc.Should().NotBeNull();
        afterSecond.LatestJob.Error.Should().Be("queue storage offline");
        afterSecond.IndexStatus.Should().Be(IndexStatus.Failed);

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("a failed job is never claimed again");
    }

    [SkippableFact]
    public async Task ProcessNext_ProcessesJobsOldestFirst_OneAtATime()
    {
        var h = await NewHarnessAsync();
        var seen = new List<(string Local, IngestSource Source, string Content, IngestOptions? Options)>();
        SucceedWithOneChunk(h, seen);
        await h.EnqueueAsync("one.txt", "1");
        await h.EnqueueAsync("two.txt", "2");

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        seen.Select(s => s.Source.LogicalPath).Should().Equal("one.txt");
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        seen.Select(s => s.Source.LogicalPath).Should().Equal("one.txt", "two.txt");
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task CancelWhileProcessing_StopsTheWorker_WithoutRequeueOrFailure_AndRemovesTempFiles()
    {
        var h = await NewHarnessAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? localPath = null;
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IngestSource s, IngestOptions? _, CancellationToken ct) =>
            {
                localPath = s.LocalPath;
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // runs until cancelled
                return new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, 1);
            });
        var created = await h.EnqueueAsync("a.txt", "a");

        var processing = h.Worker.ProcessNextAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var finished = h.Registry.CancelDocument(created.DocumentId);

        finished.Should().NotBeNull();
        await finished!.WaitAsync(TimeSpan.FromSeconds(10));
        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        // Not requeued, not failed: the document delete that follows removes the job.
        var document = await h.DocumentAsync(created.DocumentId);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Processing);
        document.LatestJob.Attempts.Should().Be(1);
        document.LatestJob.Error.Should().BeNull();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
        File.Exists(localPath!).Should().BeFalse();
        h.Registry.CancelDocument(created.DocumentId).Should().BeNull("the job is no longer active");
    }

    [SkippableFact]
    public async Task DocumentDeletedMeanwhile_IsAQuietStop()
    {
        var h = await NewHarnessAsync();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IngestSource s, IngestOptions? _, CancellationToken _) =>
            {
                await DeleteDocumentAsync(s.DocumentId); // a delete lands between the claim and the write
                throw new DocumentNotFoundException(s.DocumentId);
            });
        var created = await h.EnqueueAsync("a.txt", "a");

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        (await h.Queue.GetDocumentAsync(created.DocumentId)).Should().BeNull();
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task StartupReset_ThenClaim_ProcessesTheInterruptedJobAgain()
    {
        var h = await NewHarnessAsync();
        SucceedWithOneChunk(h, []);
        var created = await h.EnqueueAsync("a.txt", "a");
        (await h.Queue.TryClaimNextAsync("previous-process", TimeSpan.FromMinutes(10)))!.JobId.Should().Be(created.JobId);
        (await h.DocumentAsync(created.DocumentId)).IndexStatus.Should().Be(IndexStatus.Processing);

        // The new process starts: the worker resets in-flight jobs before its first claim, so the (still leased) job is not stuck.
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        var done = await h.DocumentAsync(created.DocumentId);
        done.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        done.LatestJob.Attempts.Should().Be(2);
        done.IndexStatus.Should().Be(IndexStatus.Indexed);
    }

    [SkippableFact]
    public async Task ResetInFlightJobs_RequeuesProcessingJobsAndTheirDocuments()
    {
        var h = await NewHarnessAsync();
        var first = await h.EnqueueAsync("a.txt", "a");
        var second = await h.EnqueueAsync("b.txt", "b");
        await h.Queue.TryClaimNextAsync("w", TimeSpan.FromMinutes(10));

        (await h.Queue.ResetInFlightJobsAsync()).Should().Be(1);
        (await h.Queue.ResetInFlightJobsAsync()).Should().Be(0);

        var reset = await h.DocumentAsync(first.DocumentId);
        reset.LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        reset.IndexStatus.Should().Be(IndexStatus.Queued);
        (await h.DocumentAsync(second.DocumentId)).LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        (await h.Queue.TryClaimNextAsync("w2", TimeSpan.FromMinutes(10)))!.JobId.Should().Be(first.JobId);
    }

    /// <summary>A queue whose <see cref="CompleteAsync"/> always fails, to exercise job-level (retried) failures.</summary>
    private sealed class CompleteFailingQueue(IIngestQueue inner) : IIngestQueue
    {
        public Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default) => inner.EnqueueNewDocumentsAsync(uploads, maxChunkCharacters, cancellationToken);
        public Task<ReingestResult> EnqueueReingestAsync(Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default) => inner.EnqueueReingestAsync(documentId, upload, maxChunkCharacters, cancellationToken);
        public Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default) => inner.TryClaimNextAsync(owner, lease, cancellationToken);
        public Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default) => inner.ReadContentAsync(jobId, destination, cancellationToken);
        public Task<bool> ExtendLeaseAsync(Guid jobId, string owner, TimeSpan lease, CancellationToken cancellationToken = default) => inner.ExtendLeaseAsync(jobId, owner, lease, cancellationToken);
        public Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default) => throw new IOException("queue storage offline");
        public Task<IngestJobStatus> FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default) => inner.FailAsync(jobId, error, cancellationToken);
        public Task<int> ResetInFlightJobsAsync(CancellationToken cancellationToken = default) => inner.ResetInFlightJobsAsync(cancellationToken);
        public Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) => inner.GetDocumentAsync(documentId, cancellationToken);
    }
}

public class InMemoryIngestWorkerTests : IngestWorkerBehaviorTests
{
    private InMemoryIngestQueue? _queue;

    protected override Task<IIngestQueue> CreateQueueAsync(IngestSettings settings) =>
        Task.FromResult<IIngestQueue>(_queue = new InMemoryIngestQueue(settings));

    protected override Task DeleteDocumentAsync(Guid documentId)
    {
        _queue!.RemoveDocument(documentId);
        return Task.CompletedTask;
    }
}

/// <summary>The same behaviour suite against the real SQL queue.</summary>
[Collection("SqlServer")]
public class SqlIngestWorkerTests(SqlServerFixture sql) : IngestWorkerBehaviorTests
{
    private SqlDocumentStore? _store;

    protected override async Task<IIngestQueue> CreateQueueAsync(IngestSettings settings)
    {
        var connectionString = await sql.CreateDatabaseAsync();
        _store = new SqlDocumentStore(new SqlStoreOptions { ConnectionString = connectionString });
        await _store.MigrateAsync();
        return new SqlIngestQueue(new SqlStoreOptions { ConnectionString = connectionString }, settings);
    }

    protected override async Task DeleteDocumentAsync(Guid documentId) => await _store!.DeleteDocumentAsync(documentId);
}
