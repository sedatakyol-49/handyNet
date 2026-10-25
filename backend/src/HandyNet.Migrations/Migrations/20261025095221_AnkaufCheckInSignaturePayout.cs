using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AnkaufCheckInSignaturePayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "capture_method",
                schema: "ankauf",
                table: "intakes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<Guid>(
                name: "cash_movement_id",
                schema: "ankauf",
                table: "intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cash_register_id",
                schema: "ankauf",
                table: "intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "check_in_id",
                schema: "ankauf",
                table: "intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "signature_id",
                schema: "ankauf",
                table: "intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_reference",
                schema: "pos",
                table: "cash_movements",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "checkins",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    shop_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seller = table.Column<string>(type: "jsonb", nullable: true),
                    signature_image = table.Column<byte[]>(type: "bytea", nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    intake_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkins", x => x.id);
                    table.CheckConstraint("ck_checkins_consumed", "(consumed_at IS NULL) = (intake_id IS NULL)");
                    table.CheckConstraint("ck_checkins_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_checkins_purged", "purged_at IS NULL OR (seller IS NULL AND signature_image IS NULL)");
                    table.CheckConstraint("ck_checkins_submitted", "submitted_at IS NOT NULL OR (seller IS NULL AND signature_image IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "intake_signatures",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    image = table.Column<byte[]>(type: "bytea", nullable: true),
                    image_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    data_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    binding_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    declaration_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    agreed_paid_minor = table.Column<long>(type: "bigint", nullable: true),
                    agreed_payment_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_signatures", x => x.id);
                    table.CheckConstraint("ck_intake_signatures_hashes", "length(image_sha256) = 64 AND length(data_hash) = 64 AND length(binding_hash) = 64");
                    table.CheckConstraint("ck_intake_signatures_image", "image IS NOT NULL OR anonymized_at IS NOT NULL");
                    table.CheckConstraint("ck_intake_signatures_purchase", "purpose <> 'Purchase' OR (agreed_paid_minor > 0 AND agreed_payment_method IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_intake_signatures_intakes_intake_id",
                        column: x => x.intake_id,
                        principalSchema: "ankauf",
                        principalTable: "intakes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intakes_cash_movement_id",
                schema: "ankauf",
                table: "intakes",
                column: "cash_movement_id",
                unique: true,
                filter: "cash_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_intakes_check_in_id",
                schema: "ankauf",
                table: "intakes",
                column: "check_in_id",
                unique: true,
                filter: "check_in_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_intakes_signature_id",
                schema: "ankauf",
                table: "intakes",
                column: "signature_id");

            migrationBuilder.CreateIndex(
                name: "ux_cash_movements_source",
                schema: "pos",
                table: "cash_movements",
                columns: new[] { "organization_id", "source_reference" },
                unique: true,
                filter: "source_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkins_organization_id_created_at",
                schema: "ankauf",
                table: "checkins",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_checkins_token_hash",
                schema: "ankauf",
                table: "checkins",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_signatures_intake_id",
                schema: "ankauf",
                table: "intake_signatures",
                column: "intake_id");

            migrationBuilder.CreateIndex(
                name: "ix_intake_signatures_organization_id_intake_id_signed_at",
                schema: "ankauf",
                table: "intake_signatures",
                columns: new[] { "organization_id", "intake_id", "signed_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_intakes_check_in_check_in_id",
                schema: "ankauf",
                table: "intakes",
                column: "check_in_id",
                principalSchema: "ankauf",
                principalTable: "checkins",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_intakes_intake_signature_signature_id",
                schema: "ankauf",
                table: "intakes",
                column: "signature_id",
                principalSchema: "ankauf",
                principalTable: "intake_signatures",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            AnkaufCheckInSignaturePayoutSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            AnkaufCheckInSignaturePayoutSql.Down(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_intakes_check_in_check_in_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropForeignKey(
                name: "fk_intakes_intake_signature_signature_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropTable(
                name: "checkins",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "intake_signatures",
                schema: "ankauf");

            migrationBuilder.DropIndex(
                name: "ix_intakes_cash_movement_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropIndex(
                name: "ix_intakes_check_in_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropIndex(
                name: "ix_intakes_signature_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropIndex(
                name: "ux_cash_movements_source",
                schema: "pos",
                table: "cash_movements");

            migrationBuilder.DropColumn(
                name: "capture_method",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "cash_movement_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "cash_register_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "check_in_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "signature_id",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "source_reference",
                schema: "pos",
                table: "cash_movements");
        }
    }
}
