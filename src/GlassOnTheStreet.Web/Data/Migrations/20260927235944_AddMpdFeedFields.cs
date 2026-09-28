using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMpdFeedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlternateCaseNumber",
                table: "Reports",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "CrimeCount",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpdIncidentId",
                table: "Reports",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateOnly>(
                name: "MpdReportedDate",
                table: "Reports",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpdType",
                table: "Reports",
                type: "varchar(24)",
                maxLength: 24,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NibrsCode",
                table: "Reports",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NibrsCrimeAgainst",
                table: "Reports",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NibrsGroup",
                table: "Reports",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OffenseCategory",
                table: "Reports",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ProblemFinal",
                table: "Reports",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ProblemInitial",
                table: "Reports",
                type: "varchar(400)",
                maxLength: 400,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlternateCaseNumber",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "CrimeCount",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MpdIncidentId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MpdReportedDate",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MpdType",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "NibrsCode",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "NibrsCrimeAgainst",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "NibrsGroup",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "OffenseCategory",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ProblemFinal",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ProblemInitial",
                table: "Reports");
        }
    }
}
