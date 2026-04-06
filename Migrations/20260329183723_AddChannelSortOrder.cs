using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YouTubeTool.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ChannelListChannel",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ChannelListChannel");
        }
    }
}
