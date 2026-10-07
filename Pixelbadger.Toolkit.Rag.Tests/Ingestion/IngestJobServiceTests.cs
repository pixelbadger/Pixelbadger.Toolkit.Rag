using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>
/// <see cref="IngestJobService"/> behaviour against the in-memory queue and a mocked <see cref="IContentIngester"/>,
/// calling <c>ProcessAsync</c> directly (the bus consumer is covered elsewhere).
/// </summary>
public class IngestJobServiceTests
{
    private sealed class Harness
    {
        public InMemoryIngestQueue Memory { get; } = new();
        public Mock<IContentIngester> Ingester { get; } = new();
        public IngestSettings Settings { get; } = new() { MaxAttempts = 3, MaxFileSizeBytes = 1024 * 1024 };
        public IngestJobRegistry Registry { get; } = new();
        public RecordingKeepAlive KeepAlive { get; } = new();
        public InstantTimeProvider Time { get; } = new();
        public FaultyQueue Queue { get; }
        public IngestJobService Service { get; }

        public Harness()
        {
            Queue = new FaultyQueue(Memory);
            Service = new IngestJobService(
                Queue, Ingester.Object, Settings, Registry, KeepAlive, NullLogger<IngestJobService>.Instance, Time);
        }

        public async Task<EnqueuedDocument> EnqueueAsync(string path, string content, int maxChunk = 1000) =>
            (await Memory.EnqueueNewDocumentsAsync(
                [new IngestUpload(path, content.Length, () => new MemoryStream(Encoding.UTF8.GetBytes(content)))], maxChunk))[0];

        public async Task<IngestJobDto> JobAsync(EnqueuedDocument created) =>
            (await Memory.GetDocumentAsync(created.DocumentId))!.LatestJob!;

        public void IngestReturns(int chunks, List<(string Local, IngestSource Source, string Content, IngestOptions? Options)>? seen = null) =>
            Ingester
                .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IngestSource s, IngestOptions? o, CancellationToken _) =>
                {
                    seen?.Add((s.LocalPath, s, File.ReadAllText(s.LocalPath), o));
                    return new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, chunks);
                });

        public IEnumerable<IngestJobStatus> EventStatuses(Guid jobId) =>
            Memory.Events.Where(e => e.JobId == jobId).Select(e => e.Status);
    }

    private static string JobTempDirectory(Guid jobId) => Path.Combine(Path.GetTempPath(), "pbrag-ingest", jobId.ToString("N"));

    [Fact]
    public async Task Success_IngestsTheStoredFile_RecordsChunkCount_ClearsBytes_AndPublishesProcessingThenSucceeded()
    {
        var h = new Harness();
        var seen = new List<(string Local, IngestSource Source, string Content, IngestOptions? Options)>();
        h.IngestReturns(2, seen);
        var created = await h.EnqueueAsync("docs/a.MD", "# alpha", maxChunk: 500);

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var call = seen.Single();
        call.Source.LogicalPath.Should().Be("docs/a.MD");
        call.Source.DocumentId.Should().Be(created.DocumentId);
        call.Content.Should().Be("# alpha");
        Path.GetExtension(call.Local).Should().Be(".MD");
        call.Options!.MaxChunkCharacters.Should().Be(500);
        call.Options.MaxFileSizeBytes.Should().Be(h.Settings.MaxFileSizeBytes);

        var document = (await h.Memory.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        document.LatestJob.Attempts.Should().Be(1);
        h.Memory.AllContentCleared().Should().BeTrue();
        h.EventStatuses(created.JobId).Should().Equal(IngestJobStatus.Queued, IngestJobStatus.Processing, IngestJobStatus.Succeeded);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
        File.Exists(call.Local).Should().BeFalse();
    }

    [Fact]
    public async Task ZeroChunks_IsSkipped()
    {
        var h = new Harness();
        h.IngestReturns(0);
        var created = await h.EnqueueAsync("empty.txt", "e");

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var document = (await h.Memory.GetDocumentAsync(created.DocumentId))!;
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Skipped);
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        h.EventStatuses(created.JobId).Last().Should().Be(IngestJobStatus.Skipped);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task FileProblem_FailsTheJobWithoutRetry_AndRemovesTempFiles()
    {
        var h = new Harness();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("chunk too large"));
        var created = await h.EnqueueAsync("bad.txt", "b");

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var document = (await h.Memory.GetDocumentAsync(created.DocumentId))!;
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        document.LatestJob.Error.Should().Be("chunk too large");
        document.LatestJob.Attempts.Should().Be(1);
        document.IndexStatus.Should().Be(IndexStatus.Failed);
        h.Time.Delays.Should().BeEmpty("a file problem is not retried");
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownJob_IsAQuietNoOp()
    {
        var h = new Harness();

        await h.Service.ProcessAsync(Guid.NewGuid(), CancellationToken.None);

        h.Memory.Events.Should().BeEmpty();
        h.KeepAlive.Calls.Should().BeEmpty();
        h.Ingester.Verify(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TerminalJob_IsAQuietNoOp()
    {
        var h = new Harness();
        h.IngestReturns(1);
        var created = await h.EnqueueAsync("a.txt", "a");
        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);
        var eventsBefore = h.Memory.Events.Count;

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        h.Memory.Events.Should().HaveCount(eventsBefore);
        (await h.JobAsync(created)).Attempts.Should().Be(1);
        h.Ingester.Verify(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AttemptsExhaustedOnEntry_FailsTheJobWithoutIngesting()
    {
        var h = new Harness();
        h.Settings.MaxAttempts = 1;
        var created = await h.EnqueueAsync("a.txt", "a");
        // The first delivery began the job and the process died; this is the redelivery.
        (await h.Memory.BeginProcessingAsync(created.JobId))!.Attempts.Should().Be(1);

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var job = await h.JobAsync(created);
        job.Status.Should().Be(IngestJobStatus.Failed);
        job.Attempts.Should().Be(2);
        job.Error.Should().Be("The job was abandoned after 1 attempt(s).");
        h.Ingester.Verify(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task InfrastructureError_WithAttemptsLeft_BacksOffAndRethrows_LeavingTheJobProcessing()
    {
        var h = new Harness();
        h.Queue.FailReads = true;
        var created = await h.EnqueueAsync("a.txt", "a");

        var act = () => h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("queue storage offline");
        (await h.JobAsync(created)).Status.Should().Be(IngestJobStatus.Processing);
        h.Time.Delays.Should().Equal(h.Settings.PollInterval);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
        h.Registry.CancelDocument(created.DocumentId).Should().BeNull("the job is no longer active");
    }

    [Fact]
    public async Task InfrastructureError_Redelivered_BacksOffLongerEachTime_ThenFailsOnTheLastAttempt()
    {
        var h = new Harness();
        h.Settings.MaxAttempts = 3;
        h.Queue.FailCompleteUnlessFailed = true;
        h.IngestReturns(1);
        var created = await h.EnqueueAsync("a.txt", "a");

        // The bus redelivers immediately after a handler exception.
        for (var attempt = 1; attempt <= 2; attempt++)
            await FluentActions.Awaiting(() => h.Service.ProcessAsync(created.JobId, CancellationToken.None))
                .Should().ThrowAsync<IOException>();
        (await h.JobAsync(created)).Status.Should().Be(IngestJobStatus.Processing);

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None); // attempt 3 = MaxAttempts: no rethrow

        var job = await h.JobAsync(created);
        job.Status.Should().Be(IngestJobStatus.Failed);
        job.Attempts.Should().Be(3);
        job.Error.Should().Be("queue storage offline");
        (await h.Memory.GetDocumentAsync(created.DocumentId))!.IndexStatus.Should().Be(IndexStatus.Failed);
        h.Time.Delays.Should().Equal(h.Settings.PollInterval, h.Settings.PollInterval * 2);
        h.Memory.AllContentCleared().Should().BeTrue();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task InfrastructureError_OnTheLastAttempt_WhenRecordingTheFailureFailsToo_Rethrows()
    {
        var h = new Harness();
        h.Settings.MaxAttempts = 1;
        h.Queue.FailReads = true;
        h.Queue.FailAllCompletes = true;
        var created = await h.EnqueueAsync("a.txt", "a");

        var act = () => h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        (await h.JobAsync(created)).Status.Should().Be(IngestJobStatus.Processing);
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_PropagatesCancellation_LeavesJobProcessing_AndARedeliveryProcessesIt()
    {
        var h = new Harness();
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

        var act = () => h.Service.ProcessAsync(created.JobId, shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await h.JobAsync(created)).Status.Should().Be(IngestJobStatus.Processing);
        h.Time.Delays.Should().BeEmpty();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();

        // The message lock expired: the bus redelivers.
        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var done = await h.JobAsync(created);
        done.Status.Should().Be(IngestJobStatus.Succeeded);
        done.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task RedeliveryAfterACrash_ProcessesTheProcessingJob()
    {
        var h = new Harness();
        h.IngestReturns(1);
        var created = await h.EnqueueAsync("a.txt", "a");
        await h.Memory.BeginProcessingAsync(created.JobId); // the previous process died mid-job
        (await h.Memory.GetDocumentAsync(created.DocumentId))!.IndexStatus.Should().Be(IndexStatus.Processing);

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        var done = await h.JobAsync(created);
        done.Status.Should().Be(IngestJobStatus.Succeeded);
        done.Attempts.Should().Be(2);
        (await h.Memory.GetDocumentAsync(created.DocumentId))!.IndexStatus.Should().Be(IndexStatus.Indexed);
    }

    [Fact]
    public async Task DeleteWhileProcessing_StopsQuietly_RecordsNothing_AndRemovesTempFiles()
    {
        var h = new Harness();
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

        var processing = h.Service.ProcessAsync(created.JobId, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var finished = h.Registry.CancelDocument(created.DocumentId);

        finished.Should().NotBeNull();
        await finished!.WaitAsync(TimeSpan.FromSeconds(10));
        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        // Not failed, not retried: the document delete that follows removes the job.
        var job = await h.JobAsync(created);
        job.Status.Should().Be(IngestJobStatus.Processing);
        job.Attempts.Should().Be(1);
        job.Error.Should().BeNull();
        h.EventStatuses(created.JobId).Should().Equal(IngestJobStatus.Queued, IngestJobStatus.Processing);
        h.Time.Delays.Should().BeEmpty();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse("the waiting delete expects the temp files gone");
        File.Exists(localPath!).Should().BeFalse();
        h.Registry.CancelDocument(created.DocumentId).Should().BeNull("the job is no longer active");
    }

    [Fact]
    public async Task DocumentDeletedMeanwhile_IsAQuietStop()
    {
        var h = new Harness();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IngestSource s, IngestOptions? _, CancellationToken _) =>
            {
                h.Memory.RemoveDocument(s.DocumentId); // a delete lands between the begin and the write
                throw new DocumentNotFoundException(s.DocumentId);
            });
        var created = await h.EnqueueAsync("a.txt", "a");

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        (await h.Memory.GetDocumentAsync(created.DocumentId)).Should().BeNull();
        h.Time.Delays.Should().BeEmpty();
        Directory.Exists(JobTempDirectory(created.JobId)).Should().BeFalse();
    }

    [Fact]
    public async Task KeepAlive_IsMarkedBusyBeforeTheWork_AndNotForMissingJobs()
    {
        var h = new Harness();
        var callsWhenIngesting = new List<string>();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken _) =>
            {
                lock (h.KeepAlive.Calls) callsWhenIngesting.AddRange(h.KeepAlive.Calls);
                return new IngestResult(s.LogicalPath, s.DocumentId, Modality.Text, 1);
            });

        await h.Service.ProcessAsync(Guid.NewGuid(), CancellationToken.None);
        h.KeepAlive.Calls.Should().BeEmpty();

        var created = await h.EnqueueAsync("a.txt", "a");
        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        callsWhenIngesting.Should().Equal(["busy"], "the host must stay up while the job runs");
    }

    [Fact]
    public async Task KeepAlive_Failure_DoesNotFailTheJob()
    {
        var h = new Harness();
        h.KeepAlive.Throw = true;
        h.IngestReturns(1);
        var created = await h.EnqueueAsync("a.txt", "a");

        await h.Service.ProcessAsync(created.JobId, CancellationToken.None);

        (await h.JobAsync(created)).Status.Should().Be(IngestJobStatus.Succeeded);
        h.KeepAlive.Calls.Should().NotBeEmpty();
    }

    /// <summary>Records keep-alive calls ("busy" / "idle"); can be made to throw.</summary>
    private sealed class RecordingKeepAlive : IIngestKeepAlive
    {
        public List<string> Calls { get; } = [];
        public bool Throw { get; set; }

        public Task MarkBusyAsync(CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("busy");
            return Throw ? throw new IOException("keep-alive down") : Task.CompletedTask;
        }

        public Task MarkIdleAsync(CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("idle");
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock whose timers fire at once and which records the requested delays, so backoffs cost nothing.</summary>
    private sealed class InstantTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_delays) return _delays.ToList();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_delays) _delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Delegates to the in-memory queue, but can fail reads and completes (infrastructure errors).</summary>
    private sealed class FaultyQueue(InMemoryIngestQueue inner) : IIngestQueue
    {
        public bool FailReads { get; set; }

        /// <summary>Completing with any status but Failed throws (so a job can still be failed after the error).</summary>
        public bool FailCompleteUnlessFailed { get; set; }

        public bool FailAllCompletes { get; set; }

        public Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default) => inner.EnqueueNewDocumentsAsync(uploads, maxChunkCharacters, cancellationToken);
        public Task<ReingestResult> EnqueueReingestAsync(Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default) => inner.EnqueueReingestAsync(documentId, upload, maxChunkCharacters, cancellationToken);
        public Task<IngestJobClaim?> BeginProcessingAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.BeginProcessingAsync(jobId, cancellationToken);

        public Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default) =>
            FailReads ? throw new IOException("queue storage offline") : inner.ReadContentAsync(jobId, destination, cancellationToken);

        public Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default) =>
            FailAllCompletes || (FailCompleteUnlessFailed && outcome.Status != IngestJobStatus.Failed)
                ? throw new IOException("queue storage offline")
                : inner.CompleteAsync(jobId, outcome, cancellationToken);

        public Task<bool> HasActiveJobsAsync(CancellationToken cancellationToken = default) => inner.HasActiveJobsAsync(cancellationToken);
        public Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) => inner.GetDocumentAsync(documentId, cancellationToken);
        public Task<IngestJobPage> GetJobsAsync(int page, int pageSize, IngestJobStatus? status = null, CancellationToken cancellationToken = default) => inner.GetJobsAsync(page, pageSize, status, cancellationToken);
    }
}
