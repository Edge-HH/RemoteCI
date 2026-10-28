using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigureGuardProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppGuardProcessName",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: false,
                defaultValue: "RemoteCI.Guard");

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardRandomBackupPath",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProcessName",
                table: "AppGuardOverrides",
                type: "TEXT",
                nullable: false,
                defaultValue: "RemoteCI.Guard");

            migrationBuilder.AddColumn<bool>(
                name: "RandomBackupPath",
                table: "AppGuardOverrides",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppGuardProcessName",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardRandomBackupPath",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ProcessName",
                table: "AppGuardOverrides");

            migrationBuilder.DropColumn(
                name: "RandomBackupPath",
                table: "AppGuardOverrides");
        }
    }
}
