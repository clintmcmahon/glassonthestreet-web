using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlassOnTheStreet.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentStep",
                table: "SyncStatuses",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentStepStartedAt",
                table: "SyncStatuses",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FeedLastEditedAt",
                table: "SyncStatuses",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentStep",
                table: "SyncStatuses");

            migrationBuilder.DropColumn(
                name: "CurrentStepStartedAt",
                table: "SyncStatuses");

            migrationBuilder.DropColumn(
                name: "FeedLastEditedAt",
                table: "SyncStatuses");
        }
    }
}
