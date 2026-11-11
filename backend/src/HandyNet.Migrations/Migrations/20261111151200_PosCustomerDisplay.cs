using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosCustomerDisplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "display_links",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_links", x => x.id);
                    table.CheckConstraint("ck_display_links_expiry", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_display_links_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_display_links_organization_id_register_id",
                schema: "pos",
                table: "display_links",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_display_links_token_hash",
                schema: "pos",
                table: "display_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_display_links_one_active",
                schema: "pos",
                table: "display_links",
                column: "register_id",
                unique: true,
                filter: "revoked_at IS NULL");

            PosCustomerDisplaySql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosCustomerDisplaySql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "display_links",
                schema: "pos");
        }
    }
}
