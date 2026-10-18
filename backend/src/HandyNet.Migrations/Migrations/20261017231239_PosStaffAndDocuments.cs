using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosStaffAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_lock_minutes",
                schema: "pos",
                table: "registers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cashier_pins",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashier_pins", x => new { x.organization_id, x.user_id });
                    table.CheckConstraint("ck_cashier_pins_attempts", "failed_attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_cashier_pins_hash", "pin_hash IS NULL OR pin_hash LIKE 'pbkdf2-sha256$%'");
                    table.ForeignKey(
                        name: "fk_cashier_pins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cashier_sessions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashier_sessions", x => x.id);
                    table.CheckConstraint("ck_cashier_sessions_end", "(ended_at IS NULL) = (ended_reason IS NULL)");
                    table.CheckConstraint("ck_cashier_sessions_times", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_cashier_sessions_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cashier_sessions_users_cashier_user_id",
                        column: x => x.cashier_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cashier_sessions_users_device_user_id",
                        column: x => x.device_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_registers_auto_lock",
                schema: "pos",
                table: "registers",
                sql: "auto_lock_minutes BETWEEN 1 AND 240");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_pins_user_id",
                schema: "pos",
                table: "cashier_pins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_cashier_user_id",
                schema: "pos",
                table: "cashier_sessions",
                column: "cashier_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_device_user_id",
                schema: "pos",
                table: "cashier_sessions",
                column: "device_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_organization_id_register_id_device_user_id",
                schema: "pos",
                table: "cashier_sessions",
                columns: new[] { "organization_id", "register_id", "device_user_id" },
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_token_hash",
                schema: "pos",
                table: "cashier_sessions",
                column: "token_hash",
                unique: true);

            PosStaffAndDocumentsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosStaffAndDocumentsSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "cashier_pins",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cashier_sessions",
                schema: "pos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registers_auto_lock",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropColumn(
                name: "auto_lock_minutes",
                schema: "pos",
                table: "registers");
        }
    }
}
