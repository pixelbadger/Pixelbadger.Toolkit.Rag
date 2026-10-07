using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

[Collection("SqlServer")]
public class SqlIngestQueueTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly List<BusHarness> _harnesses = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var harness in _harnesses)
            await harness.DisposeAsync();
    }

    // The bus is built but never started: published events stay in the outbox, where the tests read them.
    private async Task<(SqlIngestQueue Queue, string ConnectionString)> CreateAsync(int maxAttempts = 3)
    {
        var (queue, cs, _) = await CreateWithBusAsync(maxAttempts);
        return (queue, cs);
    }

    private async Task<(SqlIngestQueue Queue, string ConnectionString, BusHarness Bus)> CreateWithBusAsync(int maxAttempts = 3)
    {
        var cs = await sql.CreateDatabaseAsync();
        var harness = await BusHarness.CreateAsync(cs);
        _harnesses.Add(harness);
        var queue = new SqlIngestQueue(new SqlStoreOptions { ConnectionString = cs }, harness.Scopes);
        return (queue, cs, harness);
    }

    private static IngestUpload Upload(string path, string content) =>
        new(path, content.Length, () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task<string> ReadContentAsync(IIngestQueue queue, Guid jobId)
    {
        using var buffer = new MemoryStream();
        await queue.ReadContentAsync(jobId, buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<EnqueuedDocument> EnqueueOneAsync(IIngestQueue queue, string path, string content, int maxChunk = 100) =>
        (await queue.EnqueueNewDocumentsAsync([Upload(path, content)], maxChunk)).Single();

    // ---- create ----

    [SkippableFact]
    public async Task EnqueueNewDocuments_CreatesADocumentAndAQueuedJobPerUpload_InOrder()
    {
        var (queue, _) = await CreateAsync();

        var created = await queue.EnqueueNewDocumentsAsync([Upload("docs/a.md", "# a"), Upload("b.txt", "bee")], 1234);

        created.Should().HaveCount(2);
        created.Select(c => c.DocumentId).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        var a = (await queue.GetDocumentAsync(created[0].DocumentId))!;
        a.Path.Should().Be("docs/a.md");
        a.Title.Should().Be("a.md");
        a.Modality.Should().Be(Modality.Text);
        a.IndexStatus.Should().Be(IndexStatus.Queued);
        a.ChunkCount.Should().Be(0);
        a.UpdatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        a.LatestJob!.JobId.Should().Be(created[0].JobId);
        a.LatestJob.Status.Should().Be(IngestJobStatus.Queued);
        a.LatestJob.Attempts.Should().Be(0);
        a.LatestJob.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        a.LatestJob.StartedAtUtc.Should().BeNull();
        (await queue.GetDocumentAsync(created[1].DocumentId))!.Path.Should().Be("b.txt");
    }

    [SkippableFact]
    public async Task EnqueueNewDocuments_AllowsTheSamePathTwice_AsTwoDocuments()
    {
        var (queue, _) = await CreateAsync();

        var created = await queue.EnqueueNewDocumentsAsync([Upload("same.txt", "one"), Upload("same.txt", "two")], 100);

        created[0].DocumentId.Should().NotBe(created[1].DocumentId);
        (await ReadContentAsync(queue, created[0].JobId)).Should().Be("one");
        (await ReadContentAsync(queue, created[1].JobId)).Should().Be("two");
    }

    [SkippableFact]
    public async Task EnqueueNewDocuments_IsAllOrNothing()
    {
        var (queue, cs) = await CreateAsync();
        var broken = new IngestUpload("broken.txt", 1, () => throw new IOException("upload stream failed"));

        var act = async () => await queue.EnqueueNewDocumentsAsync([Upload("fine.txt", "ok"), broken], 100);

        await act.Should().ThrowAsync<IOException>();
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(0);
    }

    [SkippableFact]
    public async Task GetDocument_ReturnsNull_ForUnknownId()
    {
        var (queue, _) = await CreateAsync();

        (await queue.GetDocumentAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ---- complete / content ----

    [SkippableFact]
    public async Task Complete_RecordsOutcome_NullsContent_AndMarksTheDocumentIndexed()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.md", "# a");
        await queue.BeginProcessingAsync(created.JobId);

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 4));

        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        document.LatestJob.CompletedAtUtc.Should().NotBeNull();
        (await ScalarAsync<int>(cs, $"SELECT ChunkCount FROM dbo.IngestJobs WHERE Id = '{created.JobId}'")).Should().Be(4);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL")).Should().Be(0);
        (await queue.HasActiveJobsAsync()).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Complete_SkippedAndFailedOutcomes_SetTheMatchingDocumentStatus()
    {
        var (queue, _) = await CreateAsync();
        var skipped = await EnqueueOneAsync(queue, "empty.txt", " ");
        var failed = await EnqueueOneAsync(queue, "bad.txt", "b");
        await queue.BeginProcessingAsync(skipped.JobId);
        await queue.BeginProcessingAsync(failed.JobId);

        await queue.CompleteAsync(skipped.JobId, new IngestJobOutcome(IngestJobStatus.Skipped, ChunkCount: 0));
        await queue.CompleteAsync(failed.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: "chunk too large"));

        var s = (await queue.GetDocumentAsync(skipped.DocumentId))!;
        s.IndexStatus.Should().Be(IndexStatus.Indexed);
        s.LatestJob!.Status.Should().Be(IngestJobStatus.Skipped);
        var f = (await queue.GetDocumentAsync(failed.DocumentId))!;
        f.IndexStatus.Should().Be(IndexStatus.Failed);
        f.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        f.LatestJob.Error.Should().Be("chunk too large");
    }

    [SkippableFact]
    public async Task ReadContent_StreamsTheStoredBytes_AndFailsOnceCleared()
    {
        var (queue, _) = await CreateAsync();
        var big = new string('x', 2 * 1024 * 1024) + "end";
        var created = await EnqueueOneAsync(queue, "big.txt", big);

        (await ReadContentAsync(queue, created.JobId)).Should().Be(big);

        await queue.BeginProcessingAsync(created.JobId);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        var act = async () => await queue.ReadContentAsync(created.JobId, new MemoryStream());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- re-ingest ----

    [SkippableFact]
    public async Task Reingest_UnknownDocument_IsNotFound()
    {
        var (queue, _) = await CreateAsync();

        var result = await queue.EnqueueReingestAsync(Guid.NewGuid(), Upload("a.txt", "x"), 100);

        result.Should().Be(new ReingestResult(ReingestOutcome.NotFound));
    }

    [SkippableFact]
    public async Task Reingest_OfAnIdleDocument_QueuesANewJob_AndKeepsTheHistory()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "docs/v1.md", "first");
        await queue.BeginProcessingAsync(created.JobId);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("docs/v2.txt", "second"), 555);

        result.Outcome.Should().Be(ReingestOutcome.Created);
        result.JobId.Should().NotBeNull().And.NotBe(created.JobId);
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Queued);
        document.Path.Should().Be("docs/v1.md", "path, title and modality follow the indexed version until the new job succeeds");
        document.Title.Should().Be("v1.md");
        document.LatestJob!.JobId.Should().Be(result.JobId!.Value);
        document.LatestJob.Status.Should().Be(IngestJobStatus.Queued);
        (await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.IngestJobs WHERE DocumentId = (SELECT DocumentId FROM dbo.Documents WHERE GlobalId = '{created.DocumentId}')")).Should().Be(2);
        var claim = (await queue.BeginProcessingAsync(result.JobId.Value))!;
        claim.Should().Be(new IngestJobClaim(result.JobId.Value, created.DocumentId, 1, 555, "docs/v2.txt", 6));
        (await ReadContentAsync(queue, claim.JobId)).Should().Be("second");
    }

    [SkippableFact]
    public async Task Reingest_WhileAJobIsQueued_ReplacesItsContentInPlace()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.md", "old bytes", 100);

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("b.txt", "new bytes!"), 200);

        result.Should().Be(new ReingestResult(ReingestOutcome.ReplacedQueued, created.JobId));
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(1, "no second job");
        (await ReadContentAsync(queue, created.JobId)).Should().Be("new bytes!");
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.Path.Should().Be("a.md");
        document.Modality.Should().Be(Modality.Text);
        document.IndexStatus.Should().Be(IndexStatus.Queued);
        (await queue.BeginProcessingAsync(created.JobId)).Should().Be(new IngestJobClaim(created.JobId, created.DocumentId, 1, 200, "b.txt", 10));
    }

    [SkippableFact]
    public async Task Reingest_WhileAJobIsProcessing_IsAConflict_AndChangesNothing()
    {
        var (queue, _) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.md", "running bytes");
        await queue.BeginProcessingAsync(created.JobId);

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("b.txt", "rejected"), 100);

        result.Should().Be(new ReingestResult(ReingestOutcome.Conflict));
        (await ReadContentAsync(queue, created.JobId)).Should().Be("running bytes");
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.Path.Should().Be("a.md");
        document.IndexStatus.Should().Be(IndexStatus.Processing);
    }

    [SkippableFact]
    public async Task Reingest_RacingBeginProcessing_NeverLosesTheUploadSilently()
    {
        var (queue, _) = await CreateAsync();

        for (var i = 0; i < 50; i++)
        {
            var created = await EnqueueOneAsync(queue, $"race{i}.txt", "old");
            var beginTask = Task.Run(() => queue.BeginProcessingAsync(created.JobId));
            var reingestTask = Task.Run(() => queue.EnqueueReingestAsync(created.DocumentId, Upload($"race{i}.txt", "new"), 100));
            var claim = await beginTask;
            var reingest = await reingestTask;

            claim.Should().NotBeNull("the job was queued and nothing else touches it");
            var content = await ReadContentAsync(queue, claim!.JobId);
            if (reingest.Outcome == ReingestOutcome.ReplacedQueued)
                content.Should().Be("new", "an accepted replacement is what gets processed");
            else
            {
                reingest.Outcome.Should().Be(ReingestOutcome.Conflict, "the only other answer: processing got there first");
                content.Should().Be("old");
            }

            await queue.CompleteAsync(claim.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        }
    }

    // ---- events ----

    [SkippableFact]
    public async Task EnqueueNewDocuments_PublishesQueuedPerJob_InUploadOrder()
    {
        var (queue, _, bus) = await CreateWithBusAsync();

        var created = await queue.EnqueueNewDocumentsAsync([Upload("a.txt", "a"), Upload("b.txt", "b"), Upload("c.txt", "c")], 100);

        (await bus.PublishedEventsAsync()).Should().Equal(created.Select(c => new JobStatusChanged(c.JobId, IngestJobStatus.Queued)));
    }

    [SkippableFact]
    public async Task EnqueueNewDocuments_WhenRolledBack_PublishesNothing()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var broken = new IngestUpload("broken.txt", 1, () => throw new IOException("upload stream failed"));

        var act = async () => await queue.EnqueueNewDocumentsAsync([Upload("fine.txt", "ok"), broken], 100);

        await act.Should().ThrowAsync<IOException>();
        (await bus.PublishedEventsAsync()).Should().BeEmpty("the first upload's event was written in the rolled-back transaction");
    }

    [SkippableFact]
    public async Task Reingest_PublishesQueuedOnlyForANewJob()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "one");

        var replaced = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "two"), 100);
        replaced.Outcome.Should().Be(ReingestOutcome.ReplacedQueued);
        (await bus.PublishedEventsAsync()).Should().Equal(new JobStatusChanged(created.JobId, IngestJobStatus.Queued));

        await queue.BeginProcessingAsync(created.JobId);
        (await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "three"), 100)).Outcome.Should().Be(ReingestOutcome.Conflict);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        var fresh = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "four"), 100);

        fresh.Outcome.Should().Be(ReingestOutcome.Created);
        (await bus.PublishedEventsAsync()).Should().Equal(
            new JobStatusChanged(created.JobId, IngestJobStatus.Queued),
            new JobStatusChanged(created.JobId, IngestJobStatus.Processing),
            new JobStatusChanged(created.JobId, IngestJobStatus.Succeeded),
            new JobStatusChanged(fresh.JobId!.Value, IngestJobStatus.Queued));
    }

    [SkippableFact]
    public async Task Reingest_ThatChangesNothing_PublishesNothing()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "one");
        await queue.BeginProcessingAsync(created.JobId);
        var before = (await bus.PublishedEventsAsync()).Count;

        (await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "two"), 100)).Outcome.Should().Be(ReingestOutcome.Conflict);
        (await queue.EnqueueReingestAsync(Guid.NewGuid(), Upload("a.txt", "two"), 100)).Outcome.Should().Be(ReingestOutcome.NotFound);

        (await bus.PublishedEventsAsync()).Should().HaveCount(before);
    }

    // ---- begin processing ----

    [SkippableFact]
    public async Task BeginProcessing_MovesAQueuedJobAndItsDocumentToProcessing_AndPublishes()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var first = await EnqueueOneAsync(queue, "a.txt", "a", 777);
        var second = await EnqueueOneAsync(queue, "b.txt", "b", 888);

        var claim = await queue.BeginProcessingAsync(first.JobId);

        claim.Should().Be(new IngestJobClaim(first.JobId, first.DocumentId, 1, 777, "a.txt", 1));
        var document = (await queue.GetDocumentAsync(first.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Processing);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Processing);
        document.LatestJob.Attempts.Should().Be(1);
        document.LatestJob.StartedAtUtc.Should().NotBeNull();
        (await queue.GetDocumentAsync(second.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Queued, "only the named job moves");
        var events = await bus.PublishedEventsAsync();
        events.Should().HaveCount(3);
        events[^1].Should().Be(new JobStatusChanged(first.JobId, IngestJobStatus.Processing));
    }

    [SkippableFact]
    public async Task BeginProcessing_AgainAfterACrash_IncrementsAttempts_KeepsStartedAt_AndClearsTheError()
    {
        var (queue, cs, bus) = await CreateWithBusAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        (await queue.BeginProcessingAsync(created.JobId))!.Attempts.Should().Be(1);
        var startedAt = (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.StartedAtUtc;
        await ExecAsync(cs, $"UPDATE dbo.IngestJobs SET Error = N'boom' WHERE Id = '{created.JobId}'");

        var again = await queue.BeginProcessingAsync(created.JobId);

        again.Should().Be(new IngestJobClaim(created.JobId, created.DocumentId, 2, 100, "a.txt", 1));
        var job = (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!;
        job.Status.Should().Be(IngestJobStatus.Processing);
        job.StartedAtUtc.Should().Be(startedAt);
        job.Error.Should().BeNull();
        (await bus.PublishedEventsAsync()).Count(e => e.Status == IngestJobStatus.Processing).Should().Be(2);
    }

    [SkippableFact]
    public async Task BeginProcessing_ForATerminalOrMissingJob_ReturnsNull_ChangesNothing_AndPublishesNothing()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        await queue.BeginProcessingAsync(created.JobId);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        var before = (await bus.PublishedEventsAsync()).Count;

        (await queue.BeginProcessingAsync(created.JobId)).Should().BeNull();
        (await queue.BeginProcessingAsync(Guid.NewGuid())).Should().BeNull();

        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        document.LatestJob.Attempts.Should().Be(1);
        (await bus.PublishedEventsAsync()).Should().HaveCount(before);
    }

    // ---- complete events / active jobs ----

    [SkippableFact]
    public async Task Complete_PublishesTheTerminalStatus()
    {
        var (queue, _, bus) = await CreateWithBusAsync();
        var ok = await EnqueueOneAsync(queue, "ok.txt", "o");
        var empty = await EnqueueOneAsync(queue, "empty.txt", " ");
        var bad = await EnqueueOneAsync(queue, "bad.txt", "b");

        await queue.CompleteAsync(ok.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        await queue.CompleteAsync(empty.JobId, new IngestJobOutcome(IngestJobStatus.Skipped, 0));
        await queue.CompleteAsync(bad.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: "nope"));

        (await bus.PublishedEventsAsync()).Skip(3).Should().Equal(
            new JobStatusChanged(ok.JobId, IngestJobStatus.Succeeded),
            new JobStatusChanged(empty.JobId, IngestJobStatus.Skipped),
            new JobStatusChanged(bad.JobId, IngestJobStatus.Failed));
    }

    [SkippableFact]
    public async Task Complete_ForADeletedJob_IsQuiet_AndPublishesNothing()
    {
        var (queue, cs, bus) = await CreateWithBusAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        await queue.BeginProcessingAsync(created.JobId);
        await new SqlDocumentStore(new SqlStoreOptions { ConnectionString = cs }).DeleteDocumentAsync(created.DocumentId);
        var before = (await bus.PublishedEventsAsync()).Count;

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));

        (await bus.PublishedEventsAsync()).Should().HaveCount(before);
    }

    [SkippableFact]
    public async Task Complete_RejectsANonTerminalStatus()
    {
        var (queue, _) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");

        var act = async () => await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Processing));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task HasActiveJobs_IsTrueWhileAJobIsQueuedOrProcessing()
    {
        var (queue, _) = await CreateAsync();
        (await queue.HasActiveJobsAsync()).Should().BeFalse("empty");

        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        (await queue.HasActiveJobsAsync()).Should().BeTrue("queued");

        await queue.BeginProcessingAsync(created.JobId);
        (await queue.HasActiveJobsAsync()).Should().BeTrue("processing");

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: "x"));
        (await queue.HasActiveJobsAsync()).Should().BeFalse("terminal");
    }

    // ---- cascade ----

    [SkippableFact]
    public async Task DeletingTheDocument_CascadesToItsJobs()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "again"), 100); // replaces the queued job
        var other = await EnqueueOneAsync(queue, "other.txt", "o");
        var store = new SqlDocumentStore(new SqlStoreOptions { ConnectionString = cs });

        (await store.DeleteDocumentAsync(created.DocumentId)).Should().BeTrue();

        (await queue.GetDocumentAsync(created.DocumentId)).Should().BeNull();
        (await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.IngestJobs WHERE Id = '{created.JobId}'")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(1);
        (await queue.GetDocumentAsync(other.DocumentId)).Should().NotBeNull();
        (await queue.BeginProcessingAsync(created.JobId)).Should().BeNull("the job went with its document");
        (await queue.BeginProcessingAsync(other.JobId)).Should().NotBeNull();
        (await store.DeleteDocumentAsync(created.DocumentId)).Should().BeFalse();
    }

    // ---- job listing ----

    [SkippableFact]
    public async Task GetJobs_ReturnsNewestFirst_WithTheDocumentGuidOfEachJob()
    {
        var (queue, _) = await CreateAsync();
        var first = await EnqueueOneAsync(queue, "first.txt", "1");
        await Task.Delay(20);
        var second = await EnqueueOneAsync(queue, "second.txt", "22");
        await Task.Delay(20);
        var third = await EnqueueOneAsync(queue, "third.txt", "333");

        var page = await queue.GetJobsAsync(1, 25);

        page.TotalCount.Should().Be(3);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(25);
        page.Jobs.Select(j => j.JobId).Should().Equal(third.JobId, second.JobId, first.JobId);
        page.Jobs.Select(j => j.DocumentId).Should().Equal(third.DocumentId, second.DocumentId, first.DocumentId);
        page.Jobs.Select(j => j.Path).Should().Equal("third.txt", "second.txt", "first.txt");
        var newest = page.Jobs[0];
        newest.Status.Should().Be(IngestJobStatus.Queued);
        newest.Attempts.Should().Be(0);
        newest.MaxChunkCharacters.Should().Be(100);
        newest.SizeBytes.Should().Be(3);
        newest.ChunkCount.Should().BeNull();
        newest.StartedAtUtc.Should().BeNull();
        newest.CompletedAtUtc.Should().BeNull();
        newest.Error.Should().BeNull();
        newest.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [SkippableFact]
    public async Task GetJobs_BreaksCreatedAtTies_ByIdDescending_AndPagesStably()
    {
        var (queue, cs) = await CreateAsync();
        var created = await queue.EnqueueNewDocumentsAsync(
            Enumerable.Range(0, 7).Select(i => Upload($"f{i}.txt", "x")).ToList(), 100);
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("UPDATE dbo.IngestJobs SET CreatedAtUtc = '2026-01-01T00:00:00'", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var all = (await queue.GetJobsAsync(1, 100)).Jobs.Select(j => j.JobId).ToList();
        var paged = new List<Guid>();
        for (var p = 1; p <= 3; p++)
            paged.AddRange((await queue.GetJobsAsync(p, 3)).Jobs.Select(j => j.JobId));

        all.Should().BeEquivalentTo(created.Select(c => c.JobId));
        paged.Should().Equal(all);
        // Id DESC, as SQL Server orders uniqueidentifier.
        (await GuidsAsync(cs, "SELECT Id FROM dbo.IngestJobs ORDER BY CreatedAtUtc DESC, Id DESC")).Should().Equal(all);
    }

    [SkippableFact]
    public async Task GetJobs_Paginates_AndReportsTheTotalCount()
    {
        var (queue, _) = await CreateAsync();
        var created = new List<EnqueuedDocument>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await EnqueueOneAsync(queue, $"f{i}.txt", "x"));
            await Task.Delay(20);
        }

        var page1 = await queue.GetJobsAsync(1, 2);
        var page2 = await queue.GetJobsAsync(2, 2);
        var page3 = await queue.GetJobsAsync(3, 2);
        var page4 = await queue.GetJobsAsync(4, 2);

        page1.Jobs.Select(j => j.JobId).Should().Equal(created[4].JobId, created[3].JobId);
        page2.Jobs.Select(j => j.JobId).Should().Equal(created[2].JobId, created[1].JobId);
        page3.Jobs.Select(j => j.JobId).Should().Equal(created[0].JobId);
        page4.Jobs.Should().BeEmpty();
        new[] { page1, page2, page3, page4 }.Should().OnlyContain(p => p.TotalCount == 5 && p.PageSize == 2);
        page4.Page.Should().Be(4);
    }

    [SkippableFact]
    public async Task GetJobs_FiltersByStatus_AndCountsOnlyMatches()
    {
        var (queue, _) = await CreateAsync();
        var done = await EnqueueOneAsync(queue, "done.txt", "d");
        await Task.Delay(20);
        var failed = await EnqueueOneAsync(queue, "failed.txt", "f");
        await Task.Delay(20);
        await EnqueueOneAsync(queue, "waiting.txt", "w");
        (await queue.BeginProcessingAsync(done.JobId)).Should().NotBeNull();
        await queue.CompleteAsync(done.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 4));
        (await queue.BeginProcessingAsync(failed.JobId)).Should().NotBeNull();
        await queue.CompleteAsync(failed.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: "bad file"));

        var succeeded = await queue.GetJobsAsync(1, 25, IngestJobStatus.Succeeded);
        var failedPage = await queue.GetJobsAsync(1, 25, IngestJobStatus.Failed);
        var queued = await queue.GetJobsAsync(1, 25, IngestJobStatus.Queued);
        var processing = await queue.GetJobsAsync(1, 25, IngestJobStatus.Processing);

        succeeded.TotalCount.Should().Be(1);
        var job = succeeded.Jobs.Single();
        job.JobId.Should().Be(done.JobId);
        job.DocumentId.Should().Be(done.DocumentId);
        job.ChunkCount.Should().Be(4);
        job.Attempts.Should().Be(1);
        job.CompletedAtUtc.Should().NotBeNull();
        failedPage.TotalCount.Should().Be(1);
        failedPage.Jobs.Single().Error.Should().Be("bad file");
        queued.Jobs.Single().Path.Should().Be("waiting.txt");
        processing.TotalCount.Should().Be(0);
        processing.Jobs.Should().BeEmpty();
        (await queue.GetJobsAsync(1, 25)).TotalCount.Should().Be(3);
    }

    [SkippableFact]
    public async Task GetJobs_DoesNotMaterialiseContent_AndKeepsSizeAfterTheBytesAreCleared()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "big.txt", new string('x', 5000));
        (await queue.GetJobsAsync(1, 25)).Jobs.Single().SizeBytes.Should().Be(5000);

        await queue.BeginProcessingAsync(created.JobId);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 1));

        (await ScalarAsync<int>(cs, $"SELECT CASE WHEN Content IS NULL THEN 1 ELSE 0 END FROM dbo.IngestJobs WHERE Id = '{created.JobId}'")).Should().Be(1);
        (await queue.GetJobsAsync(1, 25)).Jobs.Single().SizeBytes.Should().Be(5000);
        // The listing DTO has no byte[] member, and the SQL projection selects only its fields.
        typeof(IngestJobListItemDto).GetProperties().Select(p => p.PropertyType).Should().NotContain(typeof(byte[]));
    }

    private static async Task ExecAsync(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<Guid>> GuidsAsync(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<Guid>();
        while (await reader.ReadAsync())
            values.Add(reader.GetGuid(0));
        return values;
    }
}
