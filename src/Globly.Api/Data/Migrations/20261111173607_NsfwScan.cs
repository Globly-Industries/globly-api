using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NsfwScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NsfwScanAttempts",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NsfwScannedAt",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "NsfwScore",
                table: "Posts",
                type: "real",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NsfwScanAttempts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NsfwScannedAt",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NsfwScore",
                table: "Posts");
        }
    }
}
