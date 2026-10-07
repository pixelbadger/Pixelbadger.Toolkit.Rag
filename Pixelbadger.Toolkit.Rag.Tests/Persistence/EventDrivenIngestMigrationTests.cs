using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Persistence;

[Collection("SqlServer")]
public class EventDrivenIngestMigrationTests(SqlServerFixture sql)
{
    private const string PreviousMigration = "20261007132155_QuantizedEmbeddingsQ8";

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    [SkippableFact]
    public async Task Migrating_DeletesInFlightJobs_DropsUnindexedDocuments_AndDropsTheLeaseColumns()
    {
        var cs = await sql.CreateDatabaseAsync();
        await using (var db = new RagDbContext(new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(cs).Options))
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        // Document status: 0 Queued, 1 Processing, 2 Indexed. Job status: 0 Queued, 1 Processing, 2 Succeeded.
        // fresh: never indexed, queued. busy: indexed once, re-ingest running. done: indexed, idle.
        var vector = "CAST('[' + REPLICATE('0.1,', 255) + '0.1]' AS vector(256))";
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"""
                INSERT INTO dbo.Documents (GlobalId, SourcePath, Title, Modality, ContentHash, IndexStatus, UpdatedAtUtc)
                VALUES (NEWID(), N'fresh.txt', N'fresh', 0, '', 0, SYSUTCDATETIME()),
                       (NEWID(), N'busy.txt', N'busy', 0, REPLICATE('a', 64), 1, SYSUTCDATETIME()),
                       (NEWID(), N'done.txt', N'done', 0, REPLICATE('b', 64), 2, SYSUTCDATETIME());
                INSERT INTO dbo.Chunks_EG2Q8_256 (GlobalId, DocumentId, Ordinal, Modality, ChunkText, EmbeddingModel, Embedding)
                SELECT NEWID(), DocumentId, 1, 0, N'x', N'embeddinggemma-2-q8@256', {vector} FROM dbo.Documents WHERE SourcePath IN (N'busy.txt', N'done.txt');
                INSERT INTO dbo.IngestJobs (Id, DocumentId, Status, Attempts, MaxChunkCharacters, LogicalPath, SizeBytes, CreatedAtUtc, LeaseOwner, LeaseExpiresAtUtc)
                SELECT NEWID(), DocumentId, CASE SourcePath WHEN N'fresh.txt' THEN 0 WHEN N'busy.txt' THEN 1 ELSE 2 END,
                       1, 1000, SourcePath, 1, SYSUTCDATETIME(), N'old-worker', SYSUTCDATETIME()
                FROM dbo.Documents;
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await new SqlDocumentStore(new SqlStoreOptions { ConnectionString = cs }).MigrateAsync();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs WHERE Status IN (0, 1)")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents WHERE SourcePath = N'fresh.txt'")).Should().Be(0, "never indexed");
        (await ScalarAsync<int>(cs, "SELECT IndexStatus FROM dbo.Documents WHERE SourcePath = N'busy.txt'")).Should().Be(2, "indexed before the re-ingest");
        (await ScalarAsync<int>(cs, "SELECT IndexStatus FROM dbo.Documents WHERE SourcePath = N'done.txt'")).Should().Be(2);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(1, "only the terminal job of the idle document is history");
        (await ScalarAsync<int>(cs,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IngestJobs') AND name IN ('LeaseOwner', 'LeaseExpiresAtUtc')")).Should().Be(0);
    }
}
