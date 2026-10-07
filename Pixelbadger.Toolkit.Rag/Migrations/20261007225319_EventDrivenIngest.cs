using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixelbadger.Toolkit.Rag.Migrations
{
    /// <inheritdoc />
    public partial class EventDrivenIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reads (document view, job list) run while bus-driven ingest writes the same rows; under locking read
            // committed a read can be chosen as a deadlock victim. Row versioning is Azure SQL's default; turn it on
            // for SQL Server too. ALTER DATABASE cannot run in a transaction, and only when it is off (no-op on Azure).
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
                    EXEC (N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
                """,
                suppressTransaction: true);

            // Event-driven ingest has no pickup of jobs left over from the polling worker, and an in-flight job would
            // never get its event. This project is undeployed, so pre-upgrade in-flight jobs are simply deleted
            // (Status: 0 Queued, 1 Processing; IndexStatus: 0 Queued, 1 Processing, 2 Indexed).
            migrationBuilder.Sql("DELETE FROM dbo.IngestJobs WHERE Status IN (0, 1);");

            // Documents that never got indexed (no chunks) existed only for that job.
            migrationBuilder.Sql(
                "DELETE FROM dbo.Documents WHERE IndexStatus IN (0, 1) " +
                "AND NOT EXISTS (SELECT 1 FROM dbo.Chunks_EG2Q8_256 c WHERE c.DocumentId = dbo.Documents.DocumentId);");

            // The rest were indexed before a re-ingest was queued; with that job gone they are indexed again.
            migrationBuilder.Sql("UPDATE dbo.Documents SET IndexStatus = 2 WHERE IndexStatus IN (0, 1);");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "IngestJobs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "IngestJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "IngestJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "IngestJobs",
                type: "nvarchar(128)",
                nullable: true);
        }
    }
}
