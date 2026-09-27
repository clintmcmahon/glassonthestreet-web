using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWardAndPrecinct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Precinct",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Ward",
                table: "Reports",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Precinct",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Ward",
                table: "Reports");
        }
    }
}
