using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigureGuardBackupDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppGuardBackupDirectoryName",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BackupDirectoryName",
                table: "AppGuardOverrides",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppGuardBackupDirectoryName",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "BackupDirectoryName",
                table: "AppGuardOverrides");
        }
    }
}
