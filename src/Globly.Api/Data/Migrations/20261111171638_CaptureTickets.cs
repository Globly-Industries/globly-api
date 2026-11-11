using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaptureTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaptureTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<int>(type: "integer", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SealedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureTickets_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureTickets_Sha256",
                table: "CaptureTickets",
                column: "Sha256",
                unique: true,
                filter: "\"Sha256\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureTickets_UserId_IssuedAt",
                table: "CaptureTickets",
                columns: new[] { "UserId", "IssuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureTickets");
        }
    }
}
