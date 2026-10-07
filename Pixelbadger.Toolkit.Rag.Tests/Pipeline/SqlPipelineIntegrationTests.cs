using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>
/// The full behaviour suite against the real <see cref="SqlDocumentStore"/> (with the mock embedding service).
/// Skipped while the store is still a stub (MigrateAsync throws NotImplementedException).
/// </summary>
[Collection("SqlServer")]
public class SqlPipelineIntegrationTests(SqlServerFixture sql) : PipelineBehaviorTests
{
    protected override async Task<StoreUnderTest> CreateStoreAsync()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        var options = new SqlStoreOptions { ConnectionString = connectionString };
        var store = new SqlDocumentStore(options);

        try
        {
            await store.MigrateAsync();
        }
        catch (NotImplementedException)
        {
            Skip.If(true, "SqlDocumentStore is not implemented yet (workstream D); run after integration.");
        }

        var queue = await TestIngestQueue.CreateAsync(connectionString);

        // The upload endpoint creates the document row (and its queued job) before the worker ingests into it.
        return new StoreUnderTest(store, async path =>
            (await queue.EnqueueNewDocumentsAsync([new IngestUpload(path, 1, () => new MemoryStream([1]))], 1000)).Single().DocumentId);
    }
}
