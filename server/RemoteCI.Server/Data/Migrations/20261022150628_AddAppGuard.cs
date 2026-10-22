using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppGuardBackupIntervalHours",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardEnabled",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardPreventDeletion",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardRestartAfterExit",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AppGuardRestartDelayMinutes",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AppGuardRestartOnCrash",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationName",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppGuardEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppGuardEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppGuardOverrides",
                columns: table => new
                {
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RestartAfterExit = table.Column<bool>(type: "INTEGER", nullable: false),
                    RestartDelayMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RestartOnCrash = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreventDeletion = table.Column<bool>(type: "INTEGER", nullable: false),
                    BackupIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppGuardOverrides", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AppGuardOverrides_PluginCredentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "PluginCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppGuardEvents_AtUnixMs",
                table: "AppGuardEvents",
                column: "AtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_AppGuardEvents_CredentialId_AtUnixMs",
                table: "AppGuardEvents",
                columns: new[] { "CredentialId", "AtUnixMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppGuardEvents");

            migrationBuilder.DropTable(
                name: "AppGuardOverrides");

            migrationBuilder.DropColumn(
                name: "AppGuardBackupIntervalHours",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardEnabled",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardPreventDeletion",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardRestartAfterExit",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardRestartDelayMinutes",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "AppGuardRestartOnCrash",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "OrganizationName",
                table: "SystemMetadata");
        }
    }
}
