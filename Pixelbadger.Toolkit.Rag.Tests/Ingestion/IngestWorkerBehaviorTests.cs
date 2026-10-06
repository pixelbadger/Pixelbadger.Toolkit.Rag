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

    protected sealed class Harness(IngestWorker worker, IIngestQueue queue, Mock<IContentIngester> ingester, Mock<IDocumentStore> store, IngestSettings settings)
    {
        public IngestWorker Worker { get; } = worker;
        public IIngestQueue Queue { get; } = queue;
        public Mock<IContentIngester> Ingester { get; } = ingester;
        public Mock<IDocumentStore> Store { get; } = store;
        public IngestSettings Settings { get; } = settings;

        public Task<Guid> EnqueueAsync(params (string Path, string Content)[] files) => EnqueueAsync(1000, files);

        public Task<Guid> EnqueueAsync(int maxChunk, params (string Path, string Content)[] files) =>
            Queue.EnqueueAsync(
                files.Select(f => new IngestUpload(f.Path, f.Content.Length, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.Content)))).ToList(),
                maxChunk);

        public async Task<IngestJobStatusDto> StatusAsync(Guid jobId) => (await Queue.GetJobAsync(jobId))!;
    }

    protected async Task<Harness> NewHarnessAsync(int maxAttempts = 3, int leaseSeconds = 600)
    {
        var settings = new IngestSettings { MaxAttempts = maxAttempts, LeaseSeconds = leaseSeconds, MaxFileSizeBytes = 1024 * 1024 };
        var queue = await CreateQueueAsync(settings);
        var ingester = new Mock<IContentIngester>();
        var store = new Mock<IDocumentStore>();
        var services = new ServiceCollection()
            .AddSingleton(queue)
            .AddSingleton(ingester.Object)
            .AddSingleton(store.Object)
            .BuildServiceProvider();
        var worker = new IngestWorker(
            services.GetRequiredService<IServiceScopeFactory>(), settings, new IngestWorkerSignal(), NullLogger<IngestWorker>.Instance);
        return new Harness(worker, queue, ingester, store, settings);
    }

    /// <summary>Succeeds with one chunk per file; records what the ingester saw on disk.</summary>
    private static void SucceedWithOneChunk(Harness h, List<(string Local, string Logical, string Content, IngestOptions? Options)> seen)
    {
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? o, CancellationToken _) =>
            {
                seen.Add((s.LocalPath, s.LogicalPath, File.ReadAllText(s.LocalPath), o));
                return new IngestResult(s.LogicalPath, DocumentIds.FromLogicalPath(s.LogicalPath), Modality.Text, 1);
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
    public async Task ProcessNext_IngestsEveryFile_WithOriginalExtensionAndLogicalPath_ThenCompletes()
    {
        var h = await NewHarnessAsync();
        var seen = new List<(string Local, string Logical, string Content, IngestOptions? Options)>();
        SucceedWithOneChunk(h, seen);
        var jobId = await h.EnqueueAsync(500, ("docs/a.md", "# alpha"), ("b.TXT", "beta"));

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        seen.Select(s => s.Logical).Should().Equal("docs/a.md", "b.TXT");
        seen.Select(s => s.Content).Should().Equal("# alpha", "beta");
        seen.Select(s => Path.GetExtension(s.Local)).Should().Equal(".md", ".TXT");
        seen.Should().OnlyContain(s => s.Options!.MaxChunkCharacters == 500 && s.Options.MaxFileSizeBytes == h.Settings.MaxFileSizeBytes);

        var status = await h.StatusAsync(jobId);
        status.Status.Should().Be(IngestJobStatus.Completed);
        status.Attempts.Should().Be(1);
        status.StartedAtUtc.Should().NotBeNull();
        status.CompletedAtUtc.Should().NotBeNull();
        status.Files.Should().OnlyContain(f => f.Status == IngestFileStatus.Succeeded && f.ChunkCount == 1 && f.Modality == Modality.Text);
        status.Files[0].DocumentId.Should().Be(DocumentIds.FromLogicalPath("docs/a.md"));
        status.Summary.Should().Be(new IngestJobSummaryDto(2, 0, 0));

        // The vector index is created once per job, not per file; temp files are gone.
        h.Store.Verify(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()), Times.Once);
        Directory.Exists(JobTempDirectory(jobId)).Should().BeFalse();
        seen.Should().OnlyContain(s => !File.Exists(s.Local));
    }

    [SkippableFact]
    public async Task ProcessNext_PerFileFailure_DoesNotFailTheJob()
    {
        var h = await NewHarnessAsync();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken _) =>
                s.LogicalPath == "bad.txt"
                    ? throw new InvalidOperationException("chunk too large")
                    : s.LogicalPath == "empty.txt"
                        ? new IngestResult(s.LogicalPath, "doc_e", Modality.Text, 0)
                        : new IngestResult(s.LogicalPath, "doc_g", Modality.Text, 2));
        var jobId = await h.EnqueueAsync(("good.txt", "g"), ("bad.txt", "b"), ("empty.txt", "e"), ("after.txt", "a"));

        await h.Worker.ProcessNextAsync(CancellationToken.None);

        var status = await h.StatusAsync(jobId);
        status.Status.Should().Be(IngestJobStatus.Completed);
        status.Files.Select(f => f.Status).Should().Equal(
            IngestFileStatus.Succeeded, IngestFileStatus.Failed, IngestFileStatus.Skipped, IngestFileStatus.Succeeded);
        status.Files[1].Error.Should().Be("chunk too large");
        status.Summary.Should().Be(new IngestJobSummaryDto(2, 1, 1));
        Directory.Exists(JobTempDirectory(jobId)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task ProcessNext_Shutdown_LeavesJobProcessing_AndRetrySkipsFinishedFiles()
    {
        var h = await NewHarnessAsync(leaseSeconds: 0); // a lease that is already expired when the retry claims
        using var shutdown = new CancellationTokenSource();
        var ingested = new List<string>();
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken ct) =>
            {
                ingested.Add(s.LogicalPath);
                if (s.LogicalPath == "second.txt" && !shutdown.IsCancellationRequested)
                {
                    shutdown.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return new IngestResult(s.LogicalPath, "doc", Modality.Text, 1);
            });
        var jobId = await h.EnqueueAsync(("first.txt", "1"), ("second.txt", "2"), ("third.txt", "3"));

        await h.Worker.ProcessNextAsync(shutdown.Token);

        var interrupted = await h.StatusAsync(jobId);
        interrupted.Status.Should().Be(IngestJobStatus.Processing);
        interrupted.Files.Select(f => f.Status).Should().Equal(IngestFileStatus.Succeeded, IngestFileStatus.Queued, IngestFileStatus.Queued);
        Directory.Exists(JobTempDirectory(jobId)).Should().BeFalse();

        ingested.Clear();
        await Task.Delay(50); // let the zero-length lease lapse
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();

        ingested.Should().Equal("second.txt", "third.txt");
        var done = await h.StatusAsync(jobId);
        done.Status.Should().Be(IngestJobStatus.Completed);
        done.Attempts.Should().Be(2);
        done.Files.Should().OnlyContain(f => f.Status == IngestFileStatus.Succeeded);
    }

    [SkippableFact]
    public async Task ProcessNext_JobLevelFailure_RequeuesUntilAttemptsAreExhausted()
    {
        var h = await NewHarnessAsync(maxAttempts: 2);
        h.Ingester
            .Setup(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IngestSource s, IngestOptions? _, CancellationToken _) => new IngestResult(s.LogicalPath, "doc", Modality.Text, 1));
        h.Store.Setup(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("index storage offline"));
        var jobId = await h.EnqueueAsync(("a.txt", "a"));

        await h.Worker.ProcessNextAsync(CancellationToken.None);
        var afterFirst = await h.StatusAsync(jobId);
        afterFirst.Status.Should().Be(IngestJobStatus.Queued);
        afterFirst.Attempts.Should().Be(1);
        afterFirst.Error.Should().Be("index storage offline");

        await h.Worker.ProcessNextAsync(CancellationToken.None);
        var afterSecond = await h.StatusAsync(jobId);
        afterSecond.Status.Should().Be(IngestJobStatus.Failed);
        afterSecond.Attempts.Should().Be(2);
        afterSecond.CompletedAtUtc.Should().NotBeNull();
        afterSecond.Error.Should().Be("index storage offline");

        // The second attempt skipped the already finished file.
        h.Ingester.Verify(i => i.IngestAsync(It.IsAny<IngestSource>(), It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse("a failed job is never claimed again");
    }

    [SkippableFact]
    public async Task ProcessNext_ProcessesJobsOldestFirst_OneAtATime()
    {
        var h = await NewHarnessAsync();
        var seen = new List<(string Local, string Logical, string Content, IngestOptions? Options)>();
        SucceedWithOneChunk(h, seen);
        await h.EnqueueAsync(("one.txt", "1"));
        await h.EnqueueAsync(("two.txt", "2"));

        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        seen.Select(s => s.Logical).Should().Equal("one.txt");
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
        seen.Select(s => s.Logical).Should().Equal("one.txt", "two.txt");
        (await h.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
    }
}

public class InMemoryIngestWorkerTests : IngestWorkerBehaviorTests
{
    protected override Task<IIngestQueue> CreateQueueAsync(IngestSettings settings) =>
        Task.FromResult<IIngestQueue>(new InMemoryIngestQueue(settings));
}

/// <summary>The same behaviour suite against the real SQL queue.</summary>
[Collection("SqlServer")]
public class SqlIngestWorkerTests(SqlServerFixture sql) : IngestWorkerBehaviorTests
{
    protected override async Task<IIngestQueue> CreateQueueAsync(IngestSettings settings)
    {
        var connectionString = await sql.CreateDatabaseAsync();
        await new SqlDocumentStore(new SqlStoreOptions { ConnectionString = connectionString }).MigrateAsync();
        return new SqlIngestQueue(new SqlStoreOptions { ConnectionString = connectionString }, settings);
    }
}
