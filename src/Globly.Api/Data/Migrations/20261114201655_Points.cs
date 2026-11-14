using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Points : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_UserId",
                table: "Posts");

            migrationBuilder.AddColumn<DateTime>(
                name: "SpotlightUntil",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Points",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PointsLedger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PointsLedger_AspNetUsers_SourceUserId",
                        column: x => x.SourceUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PointsLedger_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PointsLedger_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UserId_SpotlightUntil",
                table: "Posts",
                columns: new[] { "UserId", "SpotlightUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedger_LikeOnce",
                table: "PointsLedger",
                columns: new[] { "PostId", "SourceUserId" },
                unique: true,
                filter: "\"Kind\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedger_SourceUserId",
                table: "PointsLedger",
                column: "SourceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedger_UserId_CreatedAt",
                table: "PointsLedger",
                columns: new[] { "UserId", "CreatedAt" });

            // Existing likes pay out once, by the same rules as new ones.
            migrationBuilder.Sql("""
                INSERT INTO "PointsLedger" ("Id", "UserId", "Amount", "Kind", "PostId", "SourceUserId", "CreatedAt")
                SELECT gen_random_uuid(), p."UserId", 1, 0, v."PostId", v."UserId", v."CreatedAt"
                FROM "PostVotes" v JOIN "Posts" p ON p."Id" = v."PostId"
                WHERE v."Value" > 0 AND v."UserId" <> p."UserId" AND NOT p."IsHidden";

                UPDATE "AspNetUsers" u SET "Points" = t.total
                FROM (SELECT "UserId", SUM("Amount")::int AS total FROM "PointsLedger" GROUP BY "UserId") t
                WHERE t."UserId" = u."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PointsLedger");

            migrationBuilder.DropIndex(
                name: "IX_Posts_UserId_SpotlightUntil",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SpotlightUntil",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "Points",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UserId",
                table: "Posts",
                column: "UserId");
        }
    }
}
