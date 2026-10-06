using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixelbadger.Toolkit.Rag.Migrations
{
    /// <inheritdoc />
    public partial class DocumentGuidIdsAndFlatJobs : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Drops and recreates every table: a 2.x document id ("doc_" + path hash) cannot be turned into a server-assigned
        /// Guid, and the old path-based identity has no Guid equivalent. Upgrading therefore needs re-uploaded content
        /// and a fresh Lucene index directory.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The DiskANN vector index is not part of the model (EnsureVectorIndexAsync creates it lazily), but it would
            // block dropping the chunk table, so drop it first when it exists.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks_EG2_256')) " +
                "DROP INDEX VIX_Chunks_EG2_256_Embedding ON dbo.Chunks_EG2_256;");

            migrationBuilder.DropTable(
                name: "IngestJobFiles");

            migrationBuilder.DropTable(
                name: "IngestJobs");

            migrationBuilder.DropTable(
                name: "Chunks_EG2_256");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    DocumentId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1000)", nullable: true),
                    Modality = table.Column<byte>(type: "tinyint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", nullable: false),
                    IndexStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.DocumentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GlobalId",
                table: "Documents",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateTable(
                name: "Chunks_EG2_256",
                columns: table => new
                {
                    ChunkId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Modality = table.Column<byte>(type: "tinyint", nullable: false),
                    LocatorStart = table.Column<long>(type: "bigint", nullable: true),
                    LocatorEnd = table.Column<long>(type: "bigint", nullable: true),
                    ChunkText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(256)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks_EG2_256", x => x.ChunkId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_Chunks_EG2_256_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Doc",
                table: "Chunks_EG2_256",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_EG2_256_GlobalId",
                table: "Chunks_EG2_256",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateTable(
                name: "IngestJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxChunkCharacters = table.Column<int>(type: "int", nullable: false),
                    LogicalPath = table.Column<string>(type: "nvarchar(1024)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ChunkCount = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobs_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_Document_CreatedAtUtc",
                table: "IngestJobs",
                columns: new[] { "DocumentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_Status_CreatedAtUtc",
                table: "IngestJobs",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        /// <remarks>Restores the previous schema (empty): the Guid-id data cannot be converted back.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The DiskANN vector index is not part of the model (EnsureVectorIndexAsync creates it lazily), but it would
            // block dropping the chunk table, so drop it first when it exists.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks_EG2_256')) " +
                "DROP INDEX VIX_Chunks_EG2_256_Embedding ON dbo.Chunks_EG2_256;");

            migrationBuilder.DropTable(
                name: "IngestJobs");

            migrationBuilder.DropTable(
                name: "Chunks_EG2_256");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    DocumentId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalId = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(256)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1000)", nullable: true),
                    Modality = table.Column<byte>(type: "tinyint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", nullable: false),
                    IndexStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.DocumentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GlobalId",
                table: "Documents",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SourceId",
                table: "Documents",
                column: "SourceId");

            migrationBuilder.CreateTable(
                name: "Chunks_EG2_256",
                columns: table => new
                {
                    ChunkId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Modality = table.Column<byte>(type: "tinyint", nullable: false),
                    LocatorStart = table.Column<long>(type: "bigint", nullable: true),
                    LocatorEnd = table.Column<long>(type: "bigint", nullable: true),
                    ChunkText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(256)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks_EG2_256", x => x.ChunkId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_Chunks_EG2_256_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Doc",
                table: "Chunks_EG2_256",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_EG2_256_GlobalId",
                table: "Chunks_EG2_256",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateTable(
                name: "IngestJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxChunkCharacters = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestJobFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    LogicalPath = table.Column<string>(type: "nvarchar(1024)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DocumentGlobalId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    Modality = table.Column<byte>(type: "tinyint", nullable: true),
                    ChunkCount = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobFiles_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobFiles_Job",
                table: "IngestJobFiles",
                columns: new[] { "JobId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_Status_CreatedAtUtc",
                table: "IngestJobs",
                columns: new[] { "Status", "CreatedAtUtc" });
        }
    }
}
