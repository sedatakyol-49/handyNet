using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationEmailLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_language",
                schema: "tenancy",
                table: "organizations",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "de");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_email_language",
                schema: "tenancy",
                table: "organizations",
                sql: "email_language IN ('de', 'en', 'tr', 'es', 'ru', 'ar', 'fa')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_email_language",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "email_language",
                schema: "tenancy",
                table: "organizations");
        }
    }
}
