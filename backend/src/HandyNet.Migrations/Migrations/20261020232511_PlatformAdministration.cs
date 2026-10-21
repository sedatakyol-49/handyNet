using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "disabled_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_reason",
                schema: "identity",
                table: "users",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_number",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_status_changes",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_status_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_organization_status_changes_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_status_changes_users_changed_by",
                        column: x => x.changed_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_disabled",
                schema: "identity",
                table: "users",
                sql: "(is_active AND disabled_at IS NULL AND disabled_reason IS NULL) OR (NOT is_active)");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_created_at",
                schema: "tenancy",
                table: "organizations",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_organization_status_changes_changed_by",
                schema: "tenancy",
                table: "organization_status_changes",
                column: "changed_by");

            migrationBuilder.CreateIndex(
                name: "ix_organization_status_changes_organization_id_changed_at",
                schema: "tenancy",
                table: "organization_status_changes",
                columns: new[] { "organization_id", "changed_at" });

            PlatformAdministrationSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PlatformAdministrationSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "organization_status_changes",
                schema: "tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_disabled",
                schema: "identity",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_organizations_created_at",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "disabled_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "disabled_reason",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "contact_email",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "tax_number",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "website",
                schema: "tenancy",
                table: "organizations");
        }
    }
}
