using System.Text;
using FluentAssertions;
using Moq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Pipeline;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Messaging;

/// <summary>
/// The whole event-driven pipeline on SQL: enqueue -> JobStatusChanged(Queued) -> ingest subscription ->
/// IngestJobService -> Succeeded -> vector-index subscription, with the real bus, queue, store and ingester (mock embeddings).
/// </summary>
[Collection("SqlServer")]
public class EventDrivenIngestSqlTests(SqlServerFixture sql) : IDisposable
{
    private readonly List<PipelineHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (var h in _harnesses) h.Dispose();
    }

    private async Task<BusHarness> CreateAsync(Mock<IIngestKeepAlive>? keepAlive = null)
    {
        var cs = await sql.CreateDatabaseAsync();
        var store = new SqlDocumentStore(new SqlStoreOptions { ConnectionString = cs });
        var pipeline = new PipelineHarness(store, createDocument: _ => throw new NotSupportedException());
        _harnesses.Add(pipeline);

        return await BusHarness.CreateAsync(cs, (services, _) =>
        {
            services.AddSingleton<IDocumentStore>(store);
            services.AddSingleton<IContentIngester>(pipeline.Ingester);
            services.AddSingleton<IngestJobRegistry>();
            services.AddSingleton(keepAlive?.Object ?? new NoIngestKeepAlive());
            services.AddTransient<IIngestQueue, SqlIngestQueue>();
            services.AddTransient<IIngestJobService, IngestJobService>();
            services.AddTransient<IVectorIndexService, VectorIndexService>();
        });
    }

    private static IngestUpload Upload(string path, string content) =>
        new(path, content.Length, () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static async Task<DocumentDto> WaitForAsync(IIngestQueue queue, Guid documentId, IndexStatus status)
    {
        DocumentDto? document = null;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            document = await queue.GetDocumentAsync(documentId);
            if (document?.IndexStatus == status)
                return document;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Document {documentId} is {document?.IndexStatus}, expected {status}.");
    }

    [SkippableFact]
    public async Task AnUpload_IsIngestedThroughTheBus_AndIngestGoesIdle()
    {
        var keepAlive = new Mock<IIngestKeepAlive>();
        await using var bus = await CreateAsync(keepAlive);
        await bus.StartAsync();
        var queue = bus.Services.GetRequiredService<IIngestQueue>();

        var created = await queue.EnqueueNewDocumentsAsync([Upload("docs/a.md", "# A\n\nalpha text"), Upload("b.txt", "bravo text")], 1000);

        foreach (var item in created)
        {
            var document = await WaitForAsync(queue, item.DocumentId, IndexStatus.Indexed);
            document.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
            document.LatestJob.Attempts.Should().Be(1);
            document.ChunkCount.Should().BeGreaterThan(0);
        }

        (await queue.HasActiveJobsAsync()).Should().BeFalse();
        // The vector-index subscription saw the last job finish with nothing left: the host may scale down.
        (await Eventually.TrueAsync(() => keepAlive.Invocations.Any(i => i.Method.Name == nameof(IIngestKeepAlive.MarkIdleAsync))))
            .Should().BeTrue();
        keepAlive.Verify(k => k.MarkBusyAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));

        var events = await bus.PublishedEventsAsync();
        foreach (var job in created.Select(c => c.JobId))
            events.Where(e => e.JobId == job).Select(e => e.Status).Should().Equal(
                IngestJobStatus.Queued, IngestJobStatus.Processing, IngestJobStatus.Succeeded);
    }

    [SkippableFact]
    public async Task AReingest_IsPickedUpByItsOwnEvent()
    {
        await using var bus = await CreateAsync();
        await bus.StartAsync();
        var queue = bus.Services.GetRequiredService<IIngestQueue>();
        var created = (await queue.EnqueueNewDocumentsAsync([Upload("a.txt", "version one")], 1000)).Single();
        await WaitForAsync(queue, created.DocumentId, IndexStatus.Indexed);

        var reingest = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "version two"), 1000);
        reingest.Outcome.Should().Be(ReingestOutcome.Created);

        (await Eventually.TrueAsync(() =>
            queue.GetDocumentAsync(created.DocumentId).GetAwaiter().GetResult()!.LatestJob!.JobId == reingest.JobId)).Should().BeTrue();
        var document = await WaitForAsync(queue, created.DocumentId, IndexStatus.Indexed);
        document.LatestJob!.JobId.Should().Be(reingest.JobId!.Value);
        document.LatestJob.Status.Should().Be(IngestJobStatus.Succeeded);
    }

    [SkippableFact]
    public async Task DeliveredMessages_ArePurgedAfterTheirRetention()
    {
        await using var bus = await CreateAsync();
        await bus.StartAsync();
        var queue = bus.Services.GetRequiredService<IIngestQueue>();
        var created = (await queue.EnqueueNewDocumentsAsync([Upload("a.txt", "text")], 1000)).Single();
        await WaitForAsync(queue, created.DocumentId, IndexStatus.Indexed);
        var cs = bus.Options.Sql.ConnectionString;
        (await Eventually.TrueAsync(() => CountAsync(cs, "DeliveryComplete = 1").GetAwaiter().GetResult() > 0)).Should().BeTrue();

        // Stop the consumers first: their own idle housekeeping purges too. Then age the delivered messages past the
        // retention and purge.
        await bus.Services.GetRequiredService<SlimMessageBus.Host.IConsumerControl>().Stop();
        // Age every delivered message past the retention.
        await ExecAsync(cs, $"UPDATE dbo.{JobEvents.MessagesTable} SET CreatedOn = DATEADD(DAY, -2, CreatedOn) WHERE DeliveryComplete = 1");
        var delivered = await CountAsync(cs, "DeliveryComplete = 1");

        var purged = await bus.Services.GetRequiredService<IDeliveredMessageCleanup>().PurgeAsync(CancellationToken.None);

        purged.Should().Be(delivered);
        (await CountAsync(cs, "DeliveryComplete = 1 AND CreatedOn < DATEADD(DAY, -1, SYSUTCDATETIME())")).Should().Be(0);
    }

    private static async Task<int> CountAsync(string cs, string where)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.{JobEvents.MessagesTable} WHERE {where}", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(string cs, string text)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(text, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
