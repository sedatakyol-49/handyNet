using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosGiftCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "gift_card_id",
                schema: "pos",
                table: "receipt_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gift_cards",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    code_last4 = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                    initial_minor = table.Column<long>(type: "bigint", nullable: false),
                    balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_cards", x => x.id);
                    table.UniqueConstraint("ak_gift_cards_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_gift_cards_balance", "initial_minor > 0 AND balance_minor >= 0 AND balance_minor <= initial_minor");
                    table.CheckConstraint("ck_gift_cards_code", "code_hash ~ '^[0-9a-f]{64}$' AND char_length(code_last4) = 4");
                    table.CheckConstraint("ck_gift_cards_status", "status IN ('Active', 'Voided') AND (status = 'Active' OR balance_minor = 0)");
                    table.ForeignKey(
                        name: "fk_gift_cards_customers_organization_id_customer_id",
                        columns: x => new { x.organization_id, x.customer_id },
                        principalSchema: "pos",
                        principalTable: "customers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gift_cards_receipts_organization_id_issued_receipt_id",
                        columns: x => new { x.organization_id, x.issued_receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gift_card_transactions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_minor = table.Column<long>(type: "bigint", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_card_transactions", x => x.id);
                    table.CheckConstraint("ck_gift_card_transactions_amount", "balance_after_minor >= 0 AND ((type IN ('Issue', 'Restore') AND amount_minor > 0) OR (type = 'Redeem' AND amount_minor < 0) OR (type = 'Void' AND amount_minor <= 0 AND balance_after_minor = 0) OR (type = 'Reissue' AND amount_minor = 0))");
                    table.CheckConstraint("ck_gift_card_transactions_type", "type IN ('Issue', 'Redeem', 'Restore', 'Void', 'Reissue')");
                    table.ForeignKey(
                        name: "fk_gift_card_transactions_gift_cards_organization_id_gift_card",
                        columns: x => new { x.organization_id, x.gift_card_id },
                        principalSchema: "pos",
                        principalTable: "gift_cards",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gift_card_transactions_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_lines_gift_card_id",
                schema: "pos",
                table: "receipt_lines",
                column: "gift_card_id",
                filter: "gift_card_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_transactions_gift_card_id_created_at",
                schema: "pos",
                table: "gift_card_transactions",
                columns: new[] { "gift_card_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_transactions_organization_id_gift_card_id",
                schema: "pos",
                table: "gift_card_transactions",
                columns: new[] { "organization_id", "gift_card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_transactions_organization_id_receipt_id",
                schema: "pos",
                table: "gift_card_transactions",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_transactions_receipt_id",
                schema: "pos",
                table: "gift_card_transactions",
                column: "receipt_id",
                filter: "receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_issued_receipt_id",
                schema: "pos",
                table: "gift_cards",
                column: "issued_receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_id_code_hash",
                schema: "pos",
                table: "gift_cards",
                columns: new[] { "organization_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_id_code_last4",
                schema: "pos",
                table: "gift_cards",
                columns: new[] { "organization_id", "code_last4" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_id_customer_id",
                schema: "pos",
                table: "gift_cards",
                columns: new[] { "organization_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_id_issued_at",
                schema: "pos",
                table: "gift_cards",
                columns: new[] { "organization_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_id_issued_receipt_id",
                schema: "pos",
                table: "gift_cards",
                columns: new[] { "organization_id", "issued_receipt_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_receipt_lines_gift_cards_gift_card_id",
                schema: "pos",
                table: "receipt_lines",
                column: "gift_card_id",
                principalSchema: "pos",
                principalTable: "gift_cards",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            PosGiftCardsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosGiftCardsSql.Down(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_receipt_lines_gift_cards_gift_card_id",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropTable(
                name: "gift_card_transactions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "gift_cards",
                schema: "pos");

            migrationBuilder.DropIndex(
                name: "ix_receipt_lines_gift_card_id",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropColumn(
                name: "gift_card_id",
                schema: "pos",
                table: "receipt_lines");
        }
    }
}
