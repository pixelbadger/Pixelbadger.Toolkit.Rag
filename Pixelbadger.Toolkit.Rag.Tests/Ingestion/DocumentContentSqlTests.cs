// Disabled until stream 2a ports it to IngestJobService (the polling IngestWorker is gone).
#if false
using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Pipeline;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>
/// The canonical source of a document through the real stack: SQL queue + real ingest worker + real ingester +
/// SQL store. Source bytes must become canonical only when (and together with) a successful index.
/// </summary>
[Collection("SqlServer")]
public class DocumentContentSqlTests(SqlServerFixture sql) : IDisposable
{
    private const int MaxFileSize = 64;

    private readonly List<PipelineHarness> _harnesses = [];

    private sealed record Stack(SqlIngestQueue Queue, SqlDocumentStore Store, IngestWorker Worker, string ConnectionString);

    private async Task<Stack> CreateAsync()
    {
        var cs = await sql.CreateDatabaseAsync();
        var options = new SqlStoreOptions { ConnectionString = cs };
        var store = new SqlDocumentStore(options);
        await store.MigrateAsync();
        var settings = new IngestSettings { MaxFileSizeBytes = MaxFileSize, MaxAttempts = 3 };
        var queue = new SqlIngestQueue(options, settings);

        var harness = new PipelineHarness(store, createDocument: _ => throw new NotSupportedException());
        _harnesses.Add(harness);
        var services = new ServiceCollection()
            .AddSingleton<IIngestQueue>(queue)
            .AddSingleton<IContentIngester>(harness.Ingester)
            .AddSingleton<IDocumentStore>(store)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var worker = new IngestWorker(
            scopes, settings, new IngestWorkerSignal(), new IngestJobRegistry(),
            new InFlightJobRecovery(scopes, NullLogger<InFlightJobRecovery>.Instance), new NoIngestKeepAlive(),
            NullLogger<IngestWorker>.Instance);
        return new Stack(queue, store, worker, cs);
    }

    public void Dispose()
    {
        foreach (var h in _harnesses) h.Dispose();
    }

    private static IngestUpload Upload(string path, byte[] content) =>
        new(path, content.Length, () => new MemoryStream(content));

    private static IngestUpload Upload(string path, string content) => Upload(path, Encoding.UTF8.GetBytes(content));

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static Task<int> JobsWithBytesAsync(string cs) => ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Content IS NOT NULL");

    private static async Task<string?> ContentTextAsync(IDocumentStore store, Guid documentId)
    {
        var content = await store.GetContentAsync(documentId);
        return content is null ? null : Encoding.UTF8.GetString(content.Bytes);
    }

    private static async Task RunWorkerAsync(Stack s)
    {
        (await s.Worker.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task NewlyQueuedDocument_HasNoDownloadableSource()
    {
        var s = await CreateAsync();

        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("a.txt", "queued bytes")], 1000)).Single();

        (await s.Store.GetContentAsync(created.DocumentId)).Should().BeNull("the bytes live in the job until the ingest succeeds");
        (await s.Store.GetContentAsync(Guid.NewGuid())).Should().BeNull();
        (await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Documents WHERE SourceContent IS NOT NULL")).Should().Be(0);
    }

    [SkippableFact]
    public async Task FirstSuccessfulIngest_PromotesTheJobBytes_AndStillClearsTheJob()
    {
        var s = await CreateAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x0D, 0x0A];
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("img/cat.png", png)], 1000)).Single();

        await RunWorkerAsync(s);

        var content = (await s.Store.GetContentAsync(created.DocumentId))!;
        content.Bytes.Should().Equal(png);
        content.ContentType.Should().Be("image/png");
        content.FileName.Should().Be("cat.png");
        (await s.Queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Succeeded);
        (await JobsWithBytesAsync(s.ConnectionString)).Should().Be(0, "terminal jobs never keep their bytes");
        (await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Chunks_EG2Q8_256")).Should().Be(1);
    }

    [SkippableFact]
    public async Task SuccessfulReingest_ReplacesTheCanonicalSource_AndMetadata()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("docs/v1.md", "version one")], 1000)).Single();
        await RunWorkerAsync(s);

        var reingest = await s.Queue.EnqueueReingestAsync(created.DocumentId, Upload("docs/v2.txt", "version two"), 1000);
        reingest.Outcome.Should().Be(ReingestOutcome.Created);

        // While B is queued, A stays the canonical, searchable version (and so does its metadata).
        (await ContentTextAsync(s.Store, created.DocumentId)).Should().Be("version one");
        (await s.Store.GetContentAsync(created.DocumentId))!.ContentType.Should().Be("text/markdown");
        (await s.Queue.GetDocumentAsync(created.DocumentId))!.Path.Should().Be("docs/v1.md");

        await RunWorkerAsync(s);

        var content = (await s.Store.GetContentAsync(created.DocumentId))!;
        Encoding.UTF8.GetString(content.Bytes).Should().Be("version two");
        content.ContentType.Should().Be("text/plain");
        content.FileName.Should().Be("v2.txt");
        var document = (await s.Queue.GetDocumentAsync(created.DocumentId))!;
        document.Path.Should().Be("docs/v2.txt");
        document.IndexStatus.Should().Be(IndexStatus.Indexed);
        (await JobsWithBytesAsync(s.ConnectionString)).Should().Be(0);
    }

    [SkippableFact]
    public async Task ReingestInProgress_KeepsServingTheOldSource()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("a.txt", "version one")], 1000)).Single();
        await RunWorkerAsync(s);

        await s.Queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "version two"), 1000);
        var claim = await s.Queue.TryClaimNextAsync("other-worker", TimeSpan.FromMinutes(10));

        claim.Should().NotBeNull();
        (await s.Queue.GetDocumentAsync(created.DocumentId))!.IndexStatus.Should().Be(IndexStatus.Processing);
        (await ContentTextAsync(s.Store, created.DocumentId)).Should().Be("version one");
    }

    [SkippableFact]
    public async Task FailedReingest_LeavesTheOldSourceAndChunksUntouched_ButClearsTheJobBytes()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("a.txt", "original text")], 1000)).Single();
        await RunWorkerAsync(s);
        var chunksBefore = await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Chunks_EG2Q8_256");

        // Larger than the worker's MaxFileSizeBytes, so the ingester rejects it: a Failed job, no retry.
        await s.Queue.EnqueueReingestAsync(created.DocumentId, Upload("b.md", new string('x', MaxFileSize + 1)), 1000);
        await RunWorkerAsync(s);

        var document = (await s.Queue.GetDocumentAsync(created.DocumentId))!;
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        document.Path.Should().Be("a.txt");
        (await ContentTextAsync(s.Store, created.DocumentId)).Should().Be("original text");
        (await s.Store.GetContentAsync(created.DocumentId))!.ContentType.Should().Be("text/plain");
        (await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Chunks_EG2Q8_256")).Should().Be(chunksBefore);
        (await JobsWithBytesAsync(s.ConnectionString)).Should().Be(0, "a failed job's bytes are dropped and never become the source");
    }

    [SkippableFact]
    public async Task FailedFirstIngest_LeavesTheDocumentWithoutASource()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("big.txt", new string('x', MaxFileSize + 1))], 1000)).Single();

        await RunWorkerAsync(s);

        (await s.Queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Failed);
        (await s.Store.GetContentAsync(created.DocumentId)).Should().BeNull();
    }

    [SkippableFact]
    public async Task SkippedIngest_WithNoChunks_StillPromotesTheSource()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("blank.txt", "real words")], 1000)).Single();
        await RunWorkerAsync(s);
        await s.Queue.EnqueueReingestAsync(created.DocumentId, Upload("blank.txt", "  \n \n"), 1000);

        await RunWorkerAsync(s);

        (await s.Queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.Status.Should().Be(IngestJobStatus.Skipped);
        (await ContentTextAsync(s.Store, created.DocumentId)).Should().Be("  \n \n");
        (await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Chunks_EG2Q8_256")).Should().Be(0, "source and chunks always describe the same version");
    }

    [SkippableFact]
    public async Task DeletingTheDocument_RemovesItsCanonicalContent()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("a.txt", "bytes")], 1000)).Single();
        await RunWorkerAsync(s);
        (await s.Store.GetContentAsync(created.DocumentId)).Should().NotBeNull();

        (await s.Store.DeleteDocumentAsync(created.DocumentId)).Should().BeTrue();

        (await s.Store.GetContentAsync(created.DocumentId)).Should().BeNull();
        (await ScalarAsync<int>(s.ConnectionString, "SELECT COUNT(*) FROM dbo.Documents WHERE SourceContent IS NOT NULL")).Should().Be(0);
    }

    [SkippableFact]
    public async Task DocumentView_DoesNotNeedTheBlob_AndStillWorksForDocumentsWithContent()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("a.txt", "bytes")], 1000)).Single();
        await RunWorkerAsync(s);

        var document = await s.Queue.GetDocumentAsync(created.DocumentId);

        document.Should().NotBeNull();
        document!.ChunkCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task GetContent_ForADocumentMigratedWithoutSource_IsNull()
    {
        var s = await CreateAsync();
        var created = (await s.Queue.EnqueueNewDocumentsAsync([Upload("legacy.txt", "x")], 1000)).Single();
        await RunWorkerAsync(s);
        await using (var conn = new SqlConnection(s.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("UPDATE dbo.Documents SET SourceContent = NULL, ContentType = NULL", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        (await s.Store.GetContentAsync(created.DocumentId)).Should().BeNull("legacy documents answer 404 until re-ingested");
    }
}

#endif
