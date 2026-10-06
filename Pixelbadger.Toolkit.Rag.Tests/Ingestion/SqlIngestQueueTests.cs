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
        new(path, content.Length, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    [SkippableFact]
    public async Task Enqueue_StoresJobAndFilesInOrder_AsQueued()
    {
        var (queue, _) = await CreateAsync();

        var jobId = await queue.EnqueueAsync([Upload("docs/a.md", "# a"), Upload("b.txt", "bee")], 1234);

        var job = await queue.GetJobAsync(jobId);
        job.Should().NotBeNull();
        job!.Status.Should().Be(IngestJobStatus.Queued);
        job.Attempts.Should().Be(0);
        job.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        job.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        job.StartedAtUtc.Should().BeNull();
        job.Files.Select(f => (f.Path, f.Status)).Should().Equal(("docs/a.md", IngestFileStatus.Queued), ("b.txt", IngestFileStatus.Queued));
        (await queue.GetPendingFilesAsync(jobId)).Select(f => (f.LogicalPath, f.SizeBytes)).Should().Equal(("docs/a.md", 3L), ("b.txt", 3L));
    }

    [SkippableFact]
    public async Task GetJob_ReturnsNull_ForUnknownId()
    {
        var (queue, _) = await CreateAsync();

        (await queue.GetJobAsync(Guid.NewGuid())).Should().BeNull();
    }

    [SkippableFact]
    public async Task Claim_ReturnsOldestQueuedJob_SetsLeaseAndAttempts_ThenNothing()
    {
        var (queue, cs) = await CreateAsync();
        var first = await queue.EnqueueAsync([Upload("a.txt", "a")], 777);
        var second = await queue.EnqueueAsync([Upload("b.txt", "b")], 888);

        var claim = await queue.TryClaimNextAsync("worker-1", Lease);

        claim.Should().Be(new IngestJobClaim(first, 1, 777));
        var job = (await queue.GetJobAsync(first))!;
        job.Status.Should().Be(IngestJobStatus.Processing);
        job.Attempts.Should().Be(1);
        job.StartedAtUtc.Should().NotBeNull();
        (await ScalarAsync<string>(cs, $"SELECT LeaseOwner FROM dbo.IngestJobs WHERE Id = '{first}'")).Should().Be("worker-1");

        (await queue.TryClaimNextAsync("worker-2", Lease))!.JobId.Should().Be(second);
        (await queue.TryClaimNextAsync("worker-3", Lease)).Should().BeNull("both jobs are leased");
    }

    [SkippableFact]
    public async Task ConcurrentClaimers_NeverGetTheSameJob()
    {
        var (queue, _) = await CreateAsync();
        var jobIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
            jobIds.Add(await queue.EnqueueAsync([Upload($"f{i}.txt", "x")], 100));

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
        await queue.EnqueueAsync([Upload("only.txt", "x")], 100);

        var claims = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => queue.TryClaimNextAsync($"worker-{i}", Lease))));

        claims.Count(c => c is not null).Should().Be(1);
    }

    [SkippableFact]
    public async Task Claim_ReclaimsAJobWhoseLeaseExpired_IncrementingAttempts()
    {
        var (queue, _) = await CreateAsync();
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "a")], 100);
        (await queue.TryClaimNextAsync("crashed-worker", ExpiredLease))!.Attempts.Should().Be(1);

        var reclaimed = await queue.TryClaimNextAsync("worker-2", Lease);

        reclaimed.Should().Be(new IngestJobClaim(jobId, 2, 100));
    }

    [SkippableFact]
    public async Task Claim_DoesNotTakeAJobWhileItsLeaseIsActive()
    {
        var (queue, _) = await CreateAsync();
        await queue.EnqueueAsync([Upload("a.txt", "a")], 100);
        await queue.TryClaimNextAsync("worker-1", Lease);

        (await queue.TryClaimNextAsync("worker-2", Lease)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Claim_MarksExhaustedAbandonedJobsFailed_AndDropsTheirBytes()
    {
        var (queue, cs) = await CreateAsync(maxAttempts: 2);
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "aaa"), Upload("b.txt", "bbb")], 100);
        await queue.TryClaimNextAsync("w", ExpiredLease);
        await queue.TryClaimNextAsync("w", ExpiredLease);
        (await queue.GetJobAsync(jobId))!.Attempts.Should().Be(2);

        var third = await queue.TryClaimNextAsync("w", Lease);

        third.Should().BeNull();
        var job = (await queue.GetJobAsync(jobId))!;
        job.Status.Should().Be(IngestJobStatus.Failed);
        job.CompletedAtUtc.Should().NotBeNull();
        job.Error.Should().Contain("abandoned after 2 attempt");
        job.Files.Should().OnlyContain(f => f.Status == IngestFileStatus.Failed);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobFiles WHERE Content IS NOT NULL")).Should().Be(0);
    }

    [SkippableFact]
    public async Task FailJob_RequeuesWhileAttemptsRemain_ThenFails()
    {
        var (queue, cs) = await CreateAsync(maxAttempts: 2);
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "aaa")], 100);

        await queue.TryClaimNextAsync("w", Lease);
        (await queue.FailJobAsync(jobId, "first error")).Should().Be(IngestJobStatus.Queued);
        var requeued = (await queue.GetJobAsync(jobId))!;
        requeued.Status.Should().Be(IngestJobStatus.Queued);
        requeued.Error.Should().Be("first error");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobFiles WHERE Content IS NOT NULL")).Should().Be(1, "a retry still needs the bytes");

        await queue.TryClaimNextAsync("w", Lease);
        (await queue.FailJobAsync(jobId, "second error")).Should().Be(IngestJobStatus.Failed);
        var failed = (await queue.GetJobAsync(jobId))!;
        failed.Status.Should().Be(IngestJobStatus.Failed);
        failed.Error.Should().Be("second error");
        failed.Files.Single().Status.Should().Be(IngestFileStatus.Failed);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobFiles WHERE Content IS NOT NULL")).Should().Be(0);
        (await queue.TryClaimNextAsync("w", Lease)).Should().BeNull();
    }

    [SkippableFact]
    public async Task CompleteFile_RecordsOutcome_AndNullsContent()
    {
        var (queue, cs) = await CreateAsync();
        var jobId = await queue.EnqueueAsync([Upload("a.md", "# a"), Upload("b.txt", "bee")], 100);
        var files = await queue.GetPendingFilesAsync(jobId);

        await queue.CompleteFileAsync(files[0].FileId, new IngestFileOutcome(IngestFileStatus.Succeeded, "doc_1", Modality.Text, 4));

        var job = (await queue.GetJobAsync(jobId))!;
        job.Files[0].Should().Be(new IngestFileStatusDto("a.md", IngestFileStatus.Succeeded, "doc_1", Modality.Text, 4, null));
        job.Files[1].Status.Should().Be(IngestFileStatus.Queued);
        job.Summary.Should().Be(new IngestJobSummaryDto(1, 0, 0));
        (await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.IngestJobFiles WHERE Id = {files[0].FileId} AND Content IS NULL")).Should().Be(1);
        (await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.IngestJobFiles WHERE Id = {files[1].FileId} AND Content IS NOT NULL")).Should().Be(1);
        (await queue.GetPendingFilesAsync(jobId)).Select(f => f.LogicalPath).Should().Equal("b.txt");
    }

    [SkippableFact]
    public async Task CompleteFile_FailedOutcome_KeepsTheError()
    {
        var (queue, _) = await CreateAsync();
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "a")], 100);
        var file = (await queue.GetPendingFilesAsync(jobId)).Single();

        await queue.CompleteFileAsync(file.FileId, new IngestFileOutcome(IngestFileStatus.Failed, Error: "chunk too large"));

        var status = (await queue.GetJobAsync(jobId))!.Files.Single();
        status.Status.Should().Be(IngestFileStatus.Failed);
        status.Error.Should().Be("chunk too large");
        status.DocumentId.Should().BeNull();
    }

    [SkippableFact]
    public async Task ReadFileContent_StreamsTheStoredBytes_AndFailsOnceCleared()
    {
        var (queue, _) = await CreateAsync();
        var big = new string('x', 2 * 1024 * 1024) + "end";
        var jobId = await queue.EnqueueAsync([Upload("big.txt", big)], 100);
        var file = (await queue.GetPendingFilesAsync(jobId)).Single();

        using var destination = new MemoryStream();
        await queue.ReadFileContentAsync(file.FileId, destination);

        System.Text.Encoding.UTF8.GetString(destination.ToArray()).Should().Be(big);

        await queue.CompleteFileAsync(file.FileId, new IngestFileOutcome(IngestFileStatus.Succeeded));
        var act = async () => await queue.ReadFileContentAsync(file.FileId, new MemoryStream());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task ExtendLease_OnlyForTheOwnerOfAProcessingJob()
    {
        var (queue, _) = await CreateAsync();
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "a")], 100);

        (await queue.ExtendLeaseAsync(jobId, "w", Lease)).Should().BeFalse("not claimed yet");
        await queue.TryClaimNextAsync("w", ExpiredLease);

        (await queue.ExtendLeaseAsync(jobId, "someone-else", Lease)).Should().BeFalse();
        (await queue.ExtendLeaseAsync(jobId, "w", Lease)).Should().BeTrue();
        (await queue.TryClaimNextAsync("other", Lease)).Should().BeNull("the extended lease protects the job");
    }

    [SkippableFact]
    public async Task CompleteJob_MarksItCompleted_AndItIsNeverClaimedAgain()
    {
        var (queue, _) = await CreateAsync();
        var jobId = await queue.EnqueueAsync([Upload("a.txt", "a")], 100);
        await queue.TryClaimNextAsync("w", ExpiredLease);

        await queue.CompleteJobAsync(jobId);

        var job = (await queue.GetJobAsync(jobId))!;
        job.Status.Should().Be(IngestJobStatus.Completed);
        job.CompletedAtUtc.Should().NotBeNull();
        (await queue.TryClaimNextAsync("w", Lease)).Should().BeNull();
    }
}
