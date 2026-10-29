using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UpdateAutoCheck",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateAutoDownload",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdateChannel",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateAutoCheck",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "UpdateAutoDownload",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "UpdateChannel",
                table: "SystemMetadata");
        }
    }
}
