using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Persistence;

[Collection("SqlServer")]
public class QuantizedEmbeddingsMigrationTests(SqlServerFixture sql)
{
    private const string PreviousMigration = "20261007070627_DocumentSourceContent";

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task ExecAsync(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migrating_FromFp32Schema_DeletesAllContent_AndReplacesTheChunkTable()
    {
        var cs = await sql.CreateDatabaseAsync();
        await using (var db = new RagDbContext(new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(cs).Options))
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Chunks_EG2_256'")).Should().Be(1);
        await ExecAsync(cs, """
            INSERT INTO dbo.Documents (GlobalId, SourcePath, Title, Modality, ContentHash, IndexStatus, UpdatedAtUtc)
            VALUES (NEWID(), N'a.txt', N'a', 0, REPLICATE('a', 64), 2, SYSUTCDATETIME());
            DECLARE @d int = SCOPE_IDENTITY();
            INSERT INTO dbo.Chunks_EG2_256 (GlobalId, DocumentId, Ordinal, Modality, ChunkText, EmbeddingModel, Embedding)
            VALUES (NEWID(), @d, 1, 0, N'x', N'embeddinggemma-2@256', CAST('[' + REPLICATE('0.1,', 255) + '0.1]' AS vector(256)));
            INSERT INTO dbo.IngestJobs (Id, DocumentId, Status, Attempts, MaxChunkCharacters, LogicalPath, SizeBytes, CreatedAtUtc)
            VALUES (NEWID(), @d, 1, 1, 1000, N'a.txt', 1, SYSUTCDATETIME());
            """);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(1);

        await NewStore(cs).MigrateAsync();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Chunks_EG2_256'")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Chunks_EG2Q8_256'")).Should().Be(1);
        (await ScalarAsync<int>(cs,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Chunks_EG2Q8_256') AND name = 'Embedding' " +
            "AND TYPE_NAME(user_type_id) = 'vector' AND vector_dimensions = 256")).Should().Be(1);
    }

    private static SqlDocumentStore NewStore(string cs) => new(new SqlStoreOptions { ConnectionString = cs });
}
