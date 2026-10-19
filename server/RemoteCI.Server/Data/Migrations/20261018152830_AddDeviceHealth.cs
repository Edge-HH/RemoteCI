using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "UserNotifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Link",
                table: "UserNotifications",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeviceAlertNotifyAdmins",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeviceAlertNotifyClassHeads",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeviceAlertSchoolHoursOnly",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DeviceAlertThresholdMinutes",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<bool>(
                name: "DeviceAlertsEnabled",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DeviceAlertStates",
                columns: table => new
                {
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OfflineSinceUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    AlertedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAlertStates", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "DevicePresenceEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Online = table.Column<bool>(type: "INTEGER", nullable: false),
                    AtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePresenceEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePresenceEvents_AtUnixMs",
                table: "DevicePresenceEvents",
                column: "AtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_DevicePresenceEvents_CredentialId_AtUnixMs",
                table: "DevicePresenceEvents",
                columns: new[] { "CredentialId", "AtUnixMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceAlertStates");

            migrationBuilder.DropTable(
                name: "DevicePresenceEvents");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "Link",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "DeviceAlertNotifyAdmins",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "DeviceAlertNotifyClassHeads",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "DeviceAlertSchoolHoursOnly",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "DeviceAlertThresholdMinutes",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "DeviceAlertsEnabled",
                table: "SystemMetadata");
        }
    }
}
