using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosCardTerminals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_transactions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminal_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    terminal_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    terminal_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    trace_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    terminal_receipt_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    card_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    masked_pan = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    auth_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    result_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    result_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    customer_receipt_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    merchant_receipt_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversal_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_transactions", x => x.id);
                    table.CheckConstraint("ck_card_transactions_amount", "amount_minor > 0");
                    table.CheckConstraint("ck_card_transactions_completed", "(status = 'Pending') = (completed_at IS NULL)");
                    table.CheckConstraint("ck_card_transactions_kind", "kind IN ('Payment', 'Refund')");
                    table.CheckConstraint("ck_card_transactions_link", "(receipt_id IS NULL AND linked_at IS NULL) OR (receipt_id IS NOT NULL AND linked_at IS NOT NULL AND status = 'Approved')");
                    table.CheckConstraint("ck_card_transactions_reversal", "(status = 'Reversed') = (reversed_at IS NOT NULL)");
                    table.CheckConstraint("ck_card_transactions_status", "status IN ('Pending', 'Approved', 'Declined', 'Aborted', 'Failed', 'Reversed')");
                    table.ForeignKey(
                        name: "fk_card_transactions_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_card_transactions_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "terminal_profiles",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    connection = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: true),
                    bridge_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    zvt_password = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terminal_profiles", x => x.id);
                    table.CheckConstraint("ck_terminal_profiles_connection", "connection IN ('Manual', 'Simulated', 'ZvtBridge')");
                    table.CheckConstraint("ck_terminal_profiles_port", "port IS NULL OR port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_terminal_profiles_zvt", "(connection = 'ZvtBridge' AND host IS NOT NULL AND port IS NOT NULL AND bridge_url IS NOT NULL AND zvt_password ~ '^[0-9]{6}$') OR (connection <> 'ZvtBridge' AND host IS NULL AND port IS NULL AND bridge_url IS NULL AND zvt_password IS NULL)");
                    table.ForeignKey(
                        name: "fk_terminal_profiles_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_transactions_organization_id_receipt_id",
                schema: "pos",
                table: "card_transactions",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_card_transactions_organization_id_register_id_created_at",
                schema: "pos",
                table: "card_transactions",
                columns: new[] { "organization_id", "register_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_card_transactions_organization_id_status",
                schema: "pos",
                table: "card_transactions",
                columns: new[] { "organization_id", "status" },
                filter: "receipt_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_card_transactions_receipt",
                schema: "pos",
                table: "card_transactions",
                column: "receipt_id",
                unique: true,
                filter: "receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_terminal_profiles_organization_id_register_id",
                schema: "pos",
                table: "terminal_profiles",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_terminal_profiles_register_id_name",
                schema: "pos",
                table: "terminal_profiles",
                columns: new[] { "register_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_terminal_profiles_one_default",
                schema: "pos",
                table: "terminal_profiles",
                column: "register_id",
                unique: true,
                filter: "is_default");

            PosCardTerminalsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosCardTerminalsSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "card_transactions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "terminal_profiles",
                schema: "pos");
        }
    }
}
