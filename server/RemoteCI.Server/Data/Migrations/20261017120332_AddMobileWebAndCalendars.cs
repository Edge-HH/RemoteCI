using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileWebAndCalendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserId",
                table: "WebPushSubscriptions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "WebPushSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventMask",
                table: "WebPushSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CalendarSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReminderMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    OffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebPushJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventBit = table.Column<int>(type: "INTEGER", nullable: false),
                    NotificationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPushJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPushJobs_WebPushSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "WebPushSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEventVersions",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventVersions", x => new { x.SubscriptionId, x.Uid });
                    table.ForeignKey(
                        name: "FK_CalendarEventVersions_CalendarSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "CalendarSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_TokenHash",
                table: "CalendarSubscriptions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_UserId",
                table: "CalendarSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPushJobs_Finished_DueAt",
                table: "WebPushJobs",
                columns: new[] { "Finished", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebPushJobs_SubscriptionId_SourceId",
                table: "WebPushJobs",
                columns: new[] { "SubscriptionId", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventVersions");

            migrationBuilder.DropTable(
                name: "WebPushJobs");

            migrationBuilder.DropTable(
                name: "CalendarSubscriptions");

            migrationBuilder.DropColumn(
                name: "BrowserId",
                table: "WebPushSubscriptions");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "WebPushSubscriptions");

            migrationBuilder.DropColumn(
                name: "EventMask",
                table: "WebPushSubscriptions");
        }
    }
}
