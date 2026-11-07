using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommunityModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flagged posts (reported past the threshold) become hidden posts.
            migrationBuilder.DropIndex(name: "IX_Posts_IsFlagged", table: "Posts");
            migrationBuilder.RenameColumn(name: "IsFlagged", table: "Posts", newName: "IsHidden");

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenAt", table: "Posts", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "HiddenReason", table: "Posts", type: "integer", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<bool>(
                name: "ModeratorApproved", table: "Posts", type: "boolean", nullable: false, defaultValue: false);

            // HiddenReason.Reports = 1
            migrationBuilder.Sql("""UPDATE "Posts" SET "HiddenReason" = 1, "HiddenAt" = now() WHERE "IsHidden";""");

            migrationBuilder.CreateIndex(name: "IX_Posts_IsHidden", table: "Posts", column: "IsHidden");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Posts_IsHidden", table: "Posts");
            migrationBuilder.DropColumn(name: "HiddenAt", table: "Posts");
            migrationBuilder.DropColumn(name: "HiddenReason", table: "Posts");
            migrationBuilder.DropColumn(name: "ModeratorApproved", table: "Posts");
            migrationBuilder.RenameColumn(name: "IsHidden", table: "Posts", newName: "IsFlagged");
            migrationBuilder.CreateIndex(name: "IX_Posts_IsFlagged", table: "Posts", column: "IsFlagged");
        }
    }
}
