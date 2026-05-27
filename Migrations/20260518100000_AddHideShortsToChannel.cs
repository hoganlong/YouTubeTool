using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YouTubeTool.Migrations
{
    /// <inheritdoc />
    public partial class AddHideShortsToChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HideShorts",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HideShorts",
                table: "Channels");
        }
    }
}
