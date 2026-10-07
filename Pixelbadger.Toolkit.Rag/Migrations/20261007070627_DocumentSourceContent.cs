using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixelbadger.Toolkit.Rag.Migrations
{
    /// <inheritdoc />
    public partial class DocumentSourceContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Documents",
                type: "nvarchar(255)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SourceContent",
                table: "Documents",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SourceContent",
                table: "Documents");
        }
    }
}
