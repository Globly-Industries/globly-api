using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Globly.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostLocationRadius : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationRadiusMeters",
                table: "Posts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocationRadiusMeters",
                table: "Posts");
        }
    }
}
