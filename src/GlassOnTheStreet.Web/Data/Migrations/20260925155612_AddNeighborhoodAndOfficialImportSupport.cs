using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNeighborhoodAndOfficialImportSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalCaseNumber",
                table: "Reports",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Neighborhood",
                table: "Reports",
                type: "varchar(120)",
                maxLength: 120,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ExternalCaseNumber",
                table: "Reports",
                column: "ExternalCaseNumber",
                unique: true,
                filter: "`ExternalCaseNumber` IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reports_ExternalCaseNumber",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ExternalCaseNumber",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Neighborhood",
                table: "Reports");
        }
    }
}
