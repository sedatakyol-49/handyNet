using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CompanyProfileAndLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_name",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bic",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "iban",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "managing_director",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "payment_terms_days",
                schema: "tenancy",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_footer",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_header",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "register_court",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "register_number",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "tenancy",
                table: "organization_status_changes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "StatusChange");

            migrationBuilder.CreateTable(
                name: "organization_logos",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    screen_storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    screen_width = table.Column<int>(type: "integer", nullable: false),
                    screen_height = table.Column<int>(type: "integer", nullable: false),
                    screen_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    print_storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    print_width = table.Column<int>(type: "integer", nullable: false),
                    print_height = table.Column<int>(type: "integer", nullable: false),
                    print_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    original_format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    original_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_logos", x => x.id);
                    table.CheckConstraint("ck_organization_logos_removed", "(removed_at IS NULL) = (removed_by IS NULL)");
                    table.ForeignKey(
                        name: "fk_organization_logos_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_logos_users_removed_by",
                        column: x => x.removed_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_logos_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_bank_needs_iban",
                schema: "tenancy",
                table: "organizations",
                sql: "iban IS NOT NULL OR (bic IS NULL AND bank_name IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_bic",
                schema: "tenancy",
                table: "organizations",
                sql: "bic ~ '^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_iban",
                schema: "tenancy",
                table: "organizations",
                sql: "iban ~ '^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_payment_terms",
                schema: "tenancy",
                table: "organizations",
                sql: "payment_terms_days BETWEEN 0 AND 365");

            migrationBuilder.CreateIndex(
                name: "ix_organization_logos_removed_by",
                schema: "tenancy",
                table: "organization_logos",
                column: "removed_by");

            migrationBuilder.CreateIndex(
                name: "ix_organization_logos_uploaded_by",
                schema: "tenancy",
                table: "organization_logos",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ux_organization_logos_active",
                schema: "tenancy",
                table: "organization_logos",
                column: "organization_id",
                unique: true,
                filter: "removed_at IS NULL");

            CompanyProfileAndLogoSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CompanyProfileAndLogoSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "organization_logos",
                schema: "tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_bank_needs_iban",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_bic",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_iban",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_payment_terms",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "bank_name",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "bic",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "iban",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "managing_director",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "payment_terms_days",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "receipt_footer",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "receipt_header",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "register_court",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "register_number",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "tenancy",
                table: "organization_status_changes");
        }
    }
}
