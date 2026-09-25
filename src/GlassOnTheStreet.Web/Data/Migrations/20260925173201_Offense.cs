using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Offense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Offense",
                table: "Reports",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Offense",
                table: "Reports");
        }
    }
}
