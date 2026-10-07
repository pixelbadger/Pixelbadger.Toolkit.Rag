using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

[Collection("SqlServer")]
public class SqlIngestQueueTests(SqlServerFixture sql)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpiredLease = TimeSpan.FromSeconds(-1);

    private async Task<(SqlIngestQueue Queue, string ConnectionString)> CreateAsync(int maxAttempts = 3)
    {
        var cs = await sql.CreateDatabaseAsync();
        var options = new SqlStoreOptions { ConnectionString = cs };
        await new SqlDocumentStore(options).MigrateAsync();
        return (new SqlIngestQueue(options, new IngestSettings { MaxAttempts = maxAttempts }), cs);
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

    // ---- claim ----

    [SkippableFact]
    public async Task Claim_ReturnsOldestQueuedJob_SetsLeaseAttemptsAndDocumentStatus_ThenNothing()
    {
        var (queue, cs) = await CreateAsync();
        var first = await EnqueueOneAsync(queue, "a.txt", "a", 777);
        var second = await EnqueueOneAsync(queue, "b.txt", "b", 888);

        var claim = await queue.TryClaimNextAsync("worker-1", Lease);

        claim.Should().Be(new IngestJobClaim(first.JobId, first.DocumentId, 1, 777, "a.txt", 1));
        var document = (await queue.GetDocumentAsync(first.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Processing);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Processing);
        document.LatestJob.Attempts.Should().Be(1);
        document.LatestJob.StartedAtUtc.Should().NotBeNull();
        (await ScalarAsync<string>(cs, $"SELECT LeaseOwner FROM dbo.IngestJobs WHERE Id = '{first.JobId}'")).Should().Be("worker-1");

        (await queue.TryClaimNextAsync("worker-2", Lease))!.JobId.Should().Be(second.JobId);
        (await queue.TryClaimNextAsync("worker-3", Lease)).Should().BeNull("both jobs are leased");
    }

    [SkippableFact]
    public async Task ConcurrentClaimers_NeverGetTheSameJob()
    {
        var (queue, _) = await CreateAsync();
        var jobIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
            jobIds.Add((await EnqueueOneAsync(queue, $"f{i}.txt", "x")).JobId);

        var claims = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => Task.Run(() => queue.TryClaimNextAsync($"worker-{i}", Lease))));

        var claimed = claims.Where(c => c is not null).Select(c => c!.JobId).ToList();
        claimed.Should().OnlyHaveUniqueItems();
        claimed.Should().BeEquivalentTo(jobIds, "every job is claimed exactly once");
    }

    [SkippableFact]
    public async Task ConcurrentClaimers_ForASingleJob_ExactlyOneWins()
    {
        var (queue, _) = await CreateAsync();
        await EnqueueOneAsync(queue, "only.txt", "x");

        var claims = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => queue.TryClaimNextAsync($"worker-{i}", Lease))));

        claims.Count(c => c is not null).Should().Be(1);
    }

    [SkippableFact]
    public async Task Claim_ReclaimsAJobWhoseLeaseExpired_IncrementingAttempts()
    {
        var (queue, _) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");
        (await queue.TryClaimNextAsync("crashed-worker", ExpiredLease))!.Attempts.Should().Be(1);

        var reclaimed = await queue.TryClaimNextAsync("worker-2", Lease);

        reclaimed.Should().Be(new IngestJobClaim(created.JobId, created.DocumentId, 2, 100, "a.txt", 1));
    }

    [SkippableFact]
    public async Task Claim_DoesNotTakeAJobWhileItsLeaseIsActive()
    {
        var (queue, _) = await CreateAsync();
        await EnqueueOneAsync(queue, "a.txt", "a");
        await queue.TryClaimNextAsync("worker-1", Lease);

        (await queue.TryClaimNextAsync("worker-2", Lease)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Claim_MarksExhaustedAbandonedJobsFailed_AndDropsTheirBytes()
    {
        var (queue, cs) = await CreateAsync(maxAttempts: 2);
        var created = await EnqueueOneAsync(queue, "a.txt", "aaa");
        await queue.TryClaimNextAsync("w", ExpiredLease);
        await queue.TryClaimNextAsync("w", ExpiredLease);
        (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.Attempts.Should().Be(2);

        var third = await queue.TryClaimNextAsync("w", Lease);

        third.Should().BeNull();
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Failed);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        document.LatestJob.CompletedAtUtc.Should().NotBeNull();
        document.LatestJob.Error.Should().Contain("abandoned after 2 attempt");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL")).Should().Be(0);
    }

    // ---- fail / complete / content / lease ----

    [SkippableFact]
    public async Task Fail_RequeuesWhileAttemptsRemain_ThenFails()
    {
        var (queue, cs) = await CreateAsync(maxAttempts: 2);
        var created = await EnqueueOneAsync(queue, "a.txt", "aaa");

        await queue.TryClaimNextAsync("w", Lease);
        (await queue.FailAsync(created.JobId, "first error")).Should().Be(IngestJobStatus.Queued);
        var requeued = (await queue.GetDocumentAsync(created.DocumentId))!;
        requeued.IndexStatus.Should().Be(IndexStatus.Queued);
        requeued.LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        requeued.LatestJob.Error.Should().Be("first error");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL")).Should().Be(1, "a retry still needs the bytes");

        await queue.TryClaimNextAsync("w", Lease);
        (await queue.FailAsync(created.JobId, "second error")).Should().Be(IngestJobStatus.Failed);
        var failed = (await queue.GetDocumentAsync(created.DocumentId))!;
        failed.IndexStatus.Should().Be(IndexStatus.Failed);
        failed.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        failed.LatestJob.Error.Should().Be("second error");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL")).Should().Be(0);
        (await queue.TryClaimNextAsync("w", Lease)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Fail_ForADeletedJob_IsANoOp()
    {
        var (queue, _) = await CreateAsync();

        (await queue.FailAsync(Guid.NewGuid(), "whatever")).Should().Be(IngestJobStatus.Failed);
    }

    [SkippableFact]
    public async Task Complete_RecordsOutcome_NullsContent_AndMarksTheDocumentIndexed()
    {
        var (queue, cs) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.md", "# a");
        await queue.TryClaimNextAsync("w", Lease);

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 4));

        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        document.LatestJob.CompletedAtUtc.Should().NotBeNull();
        (await ScalarAsync<int>(cs, $"SELECT ChunkCount FROM dbo.IngestJobs WHERE Id = '{created.JobId}'")).Should().Be(4);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL")).Should().Be(0);
        (await queue.TryClaimNextAsync("w", Lease)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Complete_SkippedAndFailedOutcomes_SetTheMatchingDocumentStatus()
    {
        var (queue, _) = await CreateAsync();
        var skipped = await EnqueueOneAsync(queue, "empty.txt", " ");
        var failed = await EnqueueOneAsync(queue, "bad.txt", "b");
        await queue.TryClaimNextAsync("w", Lease);
        await queue.TryClaimNextAsync("w", Lease);

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

        await queue.TryClaimNextAsync("w", Lease);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        var act = async () => await queue.ReadContentAsync(created.JobId, new MemoryStream());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task ExtendLease_OnlyForTheOwnerOfAProcessingJob()
    {
        var (queue, _) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.txt", "a");

        (await queue.ExtendLeaseAsync(created.JobId, "w", Lease)).Should().BeFalse("not claimed yet");
        await queue.TryClaimNextAsync("w", ExpiredLease);

        (await queue.ExtendLeaseAsync(created.JobId, "someone-else", Lease)).Should().BeFalse();
        (await queue.ExtendLeaseAsync(created.JobId, "w", Lease)).Should().BeTrue();
        (await queue.TryClaimNextAsync("other", Lease)).Should().BeNull("the extended lease protects the job");
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
        await queue.TryClaimNextAsync("w", Lease);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("docs/v2.txt", "second"), 555);

        result.Outcome.Should().Be(ReingestOutcome.Created);
        result.JobId.Should().NotBeNull().And.NotBe(created.JobId);
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.IndexStatus.Should().Be(IndexStatus.Queued);
        document.Path.Should().Be("docs/v2.txt");
        document.Title.Should().Be("v2.txt");
        document.LatestJob!.JobId.Should().Be(result.JobId!.Value);
        document.LatestJob.Status.Should().Be(IngestJobStatus.Queued);
        (await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.IngestJobs WHERE DocumentId = (SELECT DocumentId FROM dbo.Documents WHERE GlobalId = '{created.DocumentId}')")).Should().Be(2);
        var claim = (await queue.TryClaimNextAsync("w", Lease))!;
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
        document.Path.Should().Be("b.txt");
        document.Modality.Should().Be(Modality.Text);
        document.IndexStatus.Should().Be(IndexStatus.Queued);
        (await queue.TryClaimNextAsync("w", Lease)).Should().Be(new IngestJobClaim(created.JobId, created.DocumentId, 1, 200, "b.txt", 10));
    }

    [SkippableFact]
    public async Task Reingest_WhileAJobIsProcessing_IsAConflict_AndChangesNothing()
    {
        var (queue, _) = await CreateAsync();
        var created = await EnqueueOneAsync(queue, "a.md", "running bytes");
        await queue.TryClaimNextAsync("w", Lease);

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("b.txt", "rejected"), 100);

        result.Should().Be(new ReingestResult(ReingestOutcome.Conflict));
        (await ReadContentAsync(queue, created.JobId)).Should().Be("running bytes");
        var document = (await queue.GetDocumentAsync(created.DocumentId))!;
        document.Path.Should().Be("a.md");
        document.IndexStatus.Should().Be(IndexStatus.Processing);
    }

    [SkippableFact]
    public async Task Reingest_RacingAClaim_NeverLosesTheUploadSilently()
    {
        var (queue, _) = await CreateAsync();

        for (var i = 0; i < 50; i++)
        {
            var created = await EnqueueOneAsync(queue, $"race{i}.txt", "old");
            var claimTask = Task.Run(() => queue.TryClaimNextAsync($"w{i}", Lease));
            var reingestTask = Task.Run(() => queue.EnqueueReingestAsync(created.DocumentId, Upload($"race{i}.txt", "new"), 100));
            var claim = await claimTask;
            var reingest = await reingestTask;

            // A claim that lost the race (the row was locked) simply tries again.
            claim ??= await queue.TryClaimNextAsync($"w{i}", Lease);
            claim.Should().NotBeNull();
            claim!.JobId.Should().Be(created.JobId);
            var content = await ReadContentAsync(queue, claim.JobId);
            if (reingest.Outcome == ReingestOutcome.ReplacedQueued)
                content.Should().Be("new", "an accepted replacement is what gets processed");
            else
            {
                reingest.Outcome.Should().Be(ReingestOutcome.Conflict, "the only other answer: the worker got there first");
                content.Should().Be("old");
            }

            await queue.CompleteAsync(claim.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        }
    }

    // ---- startup reset / cascade ----

    [SkippableFact]
    public async Task ResetInFlightJobs_RequeuesProcessingJobsAndTheirDocuments_AndNothingElse()
    {
        var (queue, _) = await CreateAsync();
        var processing = await EnqueueOneAsync(queue, "p.txt", "p");
        var done = await EnqueueOneAsync(queue, "d.txt", "d");
        var waiting = await EnqueueOneAsync(queue, "w.txt", "w");
        await queue.TryClaimNextAsync("w", Lease); // p
        await queue.TryClaimNextAsync("w", Lease); // d
        await queue.CompleteAsync(done.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));

        (await queue.ResetInFlightJobsAsync()).Should().Be(1);

        var p = (await queue.GetDocumentAsync(processing.DocumentId))!;
        p.IndexStatus.Should().Be(IndexStatus.Queued);
        p.LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        (await queue.GetDocumentAsync(done.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        (await queue.GetDocumentAsync(waiting.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Queued);
        (await queue.ResetInFlightJobsAsync()).Should().Be(0);
        // Claimable again right away, although its lease had not expired.
        (await queue.TryClaimNextAsync("new-process", Lease))!.JobId.Should().Be(processing.JobId);
    }

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
        (await queue.TryClaimNextAsync("w", Lease))!.JobId.Should().Be(other.JobId);
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
        (await queue.TryClaimNextAsync("w", Lease))!.JobId.Should().Be(done.JobId);
        await queue.CompleteAsync(done.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 4));
        (await queue.TryClaimNextAsync("w", Lease))!.JobId.Should().Be(failed.JobId);
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

        await queue.TryClaimNextAsync("w", Lease);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, ChunkCount: 1));

        (await ScalarAsync<int>(cs, $"SELECT CASE WHEN Content IS NULL THEN 1 ELSE 0 END FROM dbo.IngestJobs WHERE Id = '{created.JobId}'")).Should().Be(1);
        (await queue.GetJobsAsync(1, 25)).Jobs.Single().SizeBytes.Should().Be(5000);
        // The listing DTO has no byte[] member, and the SQL projection selects only its fields.
        typeof(IngestJobListItemDto).GetProperties().Select(p => p.PropertyType).Should().NotContain(typeof(byte[]));
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
