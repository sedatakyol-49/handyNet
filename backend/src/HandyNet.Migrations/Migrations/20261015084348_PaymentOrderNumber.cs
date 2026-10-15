using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PaymentOrderNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_number",
                schema: "payments",
                table: "payments",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            // Backfill existing payments from their order (same format as Order.DisplayNumber). Ledger texts already
            // written keep the order id — journal entries are immutable by design.
            migrationBuilder.Sql("""
                UPDATE payments.payments p SET order_number = 'B2B-' || lpad(o.number::text, 6, '0')
                FROM ordering.orders o WHERE o.id = p.order_id AND p.order_number = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "order_number",
                schema: "payments",
                table: "payments");
        }
    }
}
