using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditRetentionDays",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.CreateTable(
                name: "CommandAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDetail = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Command = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: true),
                    ResultCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ResultMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandAuditEntries_ActorUserId_CreatedAtUnixMs",
                table: "CommandAuditEntries",
                columns: new[] { "ActorUserId", "CreatedAtUnixMs" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandAuditEntries_ClassId_CreatedAtUnixMs",
                table: "CommandAuditEntries",
                columns: new[] { "ClassId", "CreatedAtUnixMs" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandAuditEntries_CreatedAtUnixMs",
                table: "CommandAuditEntries",
                column: "CreatedAtUnixMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandAuditEntries");

            migrationBuilder.DropColumn(
                name: "AuditRetentionDays",
                table: "SystemMetadata");
        }
    }
}
