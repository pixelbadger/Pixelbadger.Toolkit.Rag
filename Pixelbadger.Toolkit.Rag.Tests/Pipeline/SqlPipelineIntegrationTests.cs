using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>
/// The full behaviour suite against the real <see cref="SqlDocumentStore"/> and a real Lucene directory.
/// Skipped while the store is still a stub (MigrateAsync throws NotImplementedException).
/// </summary>
[Collection("SqlServer")]
public class SqlPipelineIntegrationTests(SqlServerFixture sql) : PipelineBehaviorTests
{
    protected override async Task<IDocumentStore> CreateStoreAsync()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        var store = new SqlDocumentStore(new SqlStoreOptions { ConnectionString = connectionString });

        try
        {
            await store.MigrateAsync();
        }
        catch (NotImplementedException)
        {
            Skip.If(true, "SqlDocumentStore is not implemented yet (workstream D); run after integration.");
        }

        return store;
    }
}
