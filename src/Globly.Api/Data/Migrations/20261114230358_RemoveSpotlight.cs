using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSpotlight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_SpotlightUntil",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_UserId_SpotlightUntil",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SpotlightUntil",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UserId",
                table: "Posts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_UserId",
                table: "Posts");

            migrationBuilder.AddColumn<DateTime>(
                name: "SpotlightUntil",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SpotlightUntil",
                table: "Posts",
                column: "SpotlightUntil",
                filter: "\"SpotlightUntil\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UserId_SpotlightUntil",
                table: "Posts",
                columns: new[] { "UserId", "SpotlightUntil" });
        }
    }
}
