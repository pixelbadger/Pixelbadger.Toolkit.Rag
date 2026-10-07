using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixelbadger.Toolkit.Rag.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Switches to the q8 (8-bit quantised) embedding graphs. Vectors from the fp32 model are not comparable with q8 ones,
    /// so all documents, jobs and chunks are deleted and the chunk table is replaced (hand-written: not a rename).
    /// Content must be re-uploaded.
    /// </remarks>
    public partial class QuantizedEmbeddingsQ8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The DiskANN vector index is not part of the model (EnsureVectorIndexAsync creates it lazily), but it would
            // block dropping the chunk table, so drop it first when it exists.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks_EG2_256')) " +
                "DROP INDEX VIX_Chunks_EG2_256_Embedding ON dbo.Chunks_EG2_256;");

            migrationBuilder.DropTable(
                name: "Chunks_EG2_256");

            // Documents (and their jobs) are removed: their vectors came from a different model and cannot be converted.
            migrationBuilder.Sql("DELETE FROM dbo.IngestJobs;");
            migrationBuilder.Sql("DELETE FROM dbo.Documents;");

            migrationBuilder.CreateTable(
                name: "Chunks_EG2Q8_256",
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
                    table.PrimaryKey("PK_Chunks_EG2Q8_256", x => x.ChunkId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_Chunks_EG2Q8_256_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Doc",
                table: "Chunks_EG2Q8_256",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_EG2Q8_256_GlobalId",
                table: "Chunks_EG2Q8_256",
                column: "GlobalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The DiskANN vector index is not part of the model (EnsureVectorIndexAsync creates it lazily), but it would
            // block dropping the chunk table, so drop it first when it exists.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks_EG2Q8_256')) " +
                "DROP INDEX VIX_Chunks_EG2Q8_256_Embedding ON dbo.Chunks_EG2Q8_256;");

            migrationBuilder.DropTable(
                name: "Chunks_EG2Q8_256");

            // Documents (and their jobs) are removed: their vectors came from a different model and cannot be converted.
            migrationBuilder.Sql("DELETE FROM dbo.IngestJobs;");
            migrationBuilder.Sql("DELETE FROM dbo.Documents;");

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
        }
    }
}
