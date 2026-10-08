using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Messaging;

[Collection("SqlServer")]
public class JobEventBusSqlTests(SqlServerFixture sql)
{
    private async Task<(BusHarness Bus, RecordingJobServices Recorder)> CreateAsync()
    {
        var recorder = new RecordingJobServices();
        var bus = await BusHarness.CreateAsync(await sql.CreateDatabaseAsync(), (services, _) => RecordingJobServices.Register(services, recorder));
        return (bus, recorder);
    }

    [SkippableFact]
    public async Task CommittedEvents_ReachTheirSubscriptions()
    {
        var (bus, recorder) = await CreateAsync();
        await using var _ = bus;
        await bus.StartAsync();
        var jobId = Guid.CreateVersion7();

        await using (var tx = await JobEventTransaction.BeginAsync(bus.Scopes))
        {
            await tx.PublishAsync(jobId, IngestJobStatus.Queued);
            await tx.CommitAsync();
        }

        (await Eventually.TrueAsync(() => recorder.Ingested.Contains(jobId))).Should().BeTrue("the ingest subscription receives Queued");
        recorder.IndexCalls.Should().Be(0, "Queued is not terminal");

        await using (var tx = await JobEventTransaction.BeginAsync(bus.Scopes))
        {
            await tx.PublishAsync(jobId, IngestJobStatus.Succeeded);
            await tx.CommitAsync();
        }

        (await Eventually.TrueAsync(() => recorder.IndexCalls == 1)).Should().BeTrue("the vector-index subscription receives Succeeded");
        recorder.Ingested.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task EventsCommitAtomicallyWithTheWrite()
    {
        var (bus, recorder) = await CreateAsync();
        await using var _ = bus;
        await bus.StartAsync();
        var committed = Guid.CreateVersion7();
        var rolledBack = Guid.CreateVersion7();
        var cs = bus.Options.Sql.ConnectionString;

        await ExecAsync(cs, "CREATE TABLE dbo.SpikeRows (Id uniqueidentifier NOT NULL PRIMARY KEY)");

        foreach (var (id, commit) in new[] { (rolledBack, false), (committed, true) })
        {
            await using var tx = await JobEventTransaction.BeginAsync(bus.Scopes);
            await using (var insert = new SqlCommand("INSERT INTO dbo.SpikeRows (Id) VALUES (@id)", tx.Connection, tx.Transaction))
            {
                insert.Parameters.AddWithValue("@id", id);
                await insert.ExecuteNonQueryAsync();
            }
            await using (var db = tx.CreateDbContext())
                (await db.Documents.CountAsync()).Should().Be(0, "EF runs on the same connection and transaction");
            await tx.PublishAsync(id, IngestJobStatus.Queued);
            if (commit)
                await tx.CommitAsync();
        }

        (await Eventually.TrueAsync(() => recorder.Ingested.Contains(committed))).Should().BeTrue();
        await Task.Delay(TimeSpan.FromSeconds(3));
        recorder.Ingested.Should().NotContain(rolledBack);
        (await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.SpikeRows")).Should().Be(1);
    }

    [SkippableFact]
    public async Task EventsPublishedBeforeTheBusStarts_AreDeliveredOnceItDoes()
    {
        var (bus, recorder) = await CreateAsync();
        await using var _ = bus;
        var jobId = Guid.CreateVersion7();

        await using (var tx = await JobEventTransaction.BeginAsync(bus.Scopes))
        {
            await tx.PublishAsync(jobId, IngestJobStatus.Queued);
            await tx.CommitAsync();
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        recorder.Ingested.Should().BeEmpty("consumers have not started");

        await bus.StartAsync();

        (await Eventually.TrueAsync(() => recorder.Ingested.Contains(jobId))).Should().BeTrue();
    }

    [SkippableFact]
    public async Task PublishedEvents_CanBeReadBackFromTheOutbox()
    {
        var (bus, _) = await CreateAsync();
        await using var _ = bus;
        var jobId = Guid.CreateVersion7();
        (await bus.PublishedEventsAsync()).Should().BeEmpty();

        await using (var tx = await JobEventTransaction.BeginAsync(bus.Scopes))
        {
            await tx.PublishAsync(jobId, IngestJobStatus.Queued);
            await tx.PublishAsync(jobId, IngestJobStatus.Processing);
            await tx.CommitAsync();
        }

        (await bus.PublishedEventsAsync()).Should().Equal(
            new JobStatusChanged(jobId, IngestJobStatus.Queued), new JobStatusChanged(jobId, IngestJobStatus.Processing));
    }

    [SkippableFact]
    public async Task AFailingHandler_IsRedelivered()
    {
        var (bus, recorder) = await CreateAsync();
        await using var _ = bus;
        var calls = 0;
        recorder.OnIngest = (_, _) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
        await bus.StartAsync();
        var jobId = Guid.CreateVersion7();

        await using (var tx = await JobEventTransaction.BeginAsync(bus.Scopes))
        {
            await tx.PublishAsync(jobId, IngestJobStatus.Queued);
            await tx.CommitAsync();
        }

        (await Eventually.TrueAsync(() => recorder.Ingested.Contains(jobId))).Should().BeTrue();
        calls.Should().Be(2);
    }

    private static async Task ExecAsync(string cs, string text)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(text, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string cs, string text)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(text, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
