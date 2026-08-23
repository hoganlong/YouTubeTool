using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YouTubeTool.Migrations
{
    /// <inheritdoc />
    public partial class ShowOptionsPerChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IncludeMembersOnly already meant "show these" — rename rather than drop/add so
            // channels the user has already opted in keep their setting.
            migrationBuilder.RenameColumn(
                name: "IncludeMembersOnly",
                table: "Channels",
                newName: "ShowMembersOnly");

            // HideShorts inverts to ShowShorts, carried over as its negation so every channel
            // keeps behaving exactly as it did: the ones that were hiding Shorts still hide them,
            // everything else still shows them. Must run before HideShorts is dropped.
            migrationBuilder.AddColumn<bool>(
                name: "ShowShorts",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                "UPDATE Channels SET ShowShorts = CASE WHEN HideShorts = 1 THEN 0 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "HideShorts",
                table: "Channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HideShorts",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE Channels SET HideShorts = CASE WHEN ShowShorts = 1 THEN 0 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "ShowShorts",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "ShowMembersOnly",
                table: "Channels",
                newName: "IncludeMembersOnly");
        }
    }
}
