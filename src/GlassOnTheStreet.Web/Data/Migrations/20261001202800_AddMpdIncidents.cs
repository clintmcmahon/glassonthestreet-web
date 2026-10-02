using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMpdIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MpdIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ExternalKey = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CaseNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurredDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OccurredHour = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    GroupKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Offense = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OffenseCategory = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CrimeAgainst = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsCrime = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CrimeCount = table.Column<short>(type: "smallint", nullable: false),
                    Neighborhood = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Ward = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    Precinct = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    Lat = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    Lng = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    Address = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MpdIncidents", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MpdIncidents_ExternalKey",
                table: "MpdIncidents",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MpdIncidents_OccurredDate",
                table: "MpdIncidents",
                column: "OccurredDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MpdIncidents");
        }
    }
}
