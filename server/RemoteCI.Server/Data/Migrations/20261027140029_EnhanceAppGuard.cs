using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnhanceAppGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppGuardBackupPathsJson",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardEnhancedMode",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BackupPathsJson",
                table: "AppGuardOverrides",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EnhancedMode",
                table: "AppGuardOverrides",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppGuardBackupPathsJson",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardEnhancedMode",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "BackupPathsJson",
                table: "AppGuardOverrides");

            migrationBuilder.DropColumn(
                name: "EnhancedMode",
                table: "AppGuardOverrides");
        }
    }
}
