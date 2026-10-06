using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixelbadger.Toolkit.Rag.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GlobalId",
                table: "Documents",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SourceId",
                table: "Documents",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Chunks_EG2_256");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}
