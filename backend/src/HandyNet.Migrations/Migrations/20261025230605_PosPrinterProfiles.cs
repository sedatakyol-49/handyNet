using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosPrinterProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "printer_profiles",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    connection = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paper_width_mm = table.Column<int>(type: "integer", nullable: false),
                    chars_per_line = table.Column<int>(type: "integer", nullable: false),
                    auto_print = table.Column<bool>(type: "boolean", nullable: false),
                    open_drawer_on_cash = table.Column<bool>(type: "boolean", nullable: false),
                    copies = table.Column<int>(type: "integer", nullable: false),
                    cut_paper = table.Column<bool>(type: "boolean", nullable: false),
                    print_qr = table.Column<bool>(type: "boolean", nullable: false),
                    codepage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: true),
                    use_https = table.Column<bool>(type: "boolean", nullable: false),
                    epos_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bridge_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    usb_vendor_id = table.Column<int>(type: "integer", nullable: true),
                    usb_product_id = table.Column<int>(type: "integer", nullable: true),
                    baud_rate = table.Column<int>(type: "integer", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_profiles", x => x.id);
                    table.CheckConstraint("ck_printer_profiles_bridge", "connection <> 'EscPosBridge' OR bridge_url IS NOT NULL");
                    table.CheckConstraint("ck_printer_profiles_chars", "chars_per_line BETWEEN 24 AND 64");
                    table.CheckConstraint("ck_printer_profiles_codepage", "codepage IN ('Cp858', 'Pc437')");
                    table.CheckConstraint("ck_printer_profiles_connection", "connection IN ('Browser', 'EpsonEpos', 'StarWebPrnt', 'EscPosBridge', 'WebUsb', 'WebSerial')");
                    table.CheckConstraint("ck_printer_profiles_copies", "copies BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_printer_profiles_network", "connection NOT IN ('EpsonEpos', 'StarWebPrnt', 'EscPosBridge') OR host IS NOT NULL");
                    table.CheckConstraint("ck_printer_profiles_paper", "paper_width_mm IN (58, 80)");
                    table.CheckConstraint("ck_printer_profiles_port", "port IS NULL OR port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_printer_profiles_usb", "(usb_vendor_id IS NULL OR usb_vendor_id BETWEEN 0 AND 65535) AND (usb_product_id IS NULL OR usb_product_id BETWEEN 0 AND 65535)");
                    table.ForeignKey(
                        name: "fk_printer_profiles_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_printer_profiles_organization_id_register_id",
                schema: "pos",
                table: "printer_profiles",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_printer_profiles_register_id_name",
                schema: "pos",
                table: "printer_profiles",
                columns: new[] { "register_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_printer_profiles_one_default",
                schema: "pos",
                table: "printer_profiles",
                column: "register_id",
                unique: true,
                filter: "is_default");

            PosPrinterProfilesSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosPrinterProfilesSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "printer_profiles",
                schema: "pos");
        }
    }
}
