using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class OrderRequestHashAndImportDuplicateFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_hash",
                schema: "ordering",
                table: "orders",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "duplicate_of_committed_file",
                schema: "imports",
                table: "import_batches",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "request_hash",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "duplicate_of_committed_file",
                schema: "imports",
                table: "import_batches");
        }
    }
}
