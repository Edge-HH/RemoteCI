using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleSwapRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VapidPrivateKey",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VapidPublicKey",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LessonTeacherOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    TeacherName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExpectedSubject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SwapRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonTeacherOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleSwapRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Forced = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SourceIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceSubject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceTeacher = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TargetClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    TargetIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSubject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TargetTeacher = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReplacementSubjectName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApproverUserIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CounterpartClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CounterpartDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CounterpartIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AppliedPlanJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleSwapRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleSwapRequests_AspNetUsers_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SwapRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPushSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonTeacherOverrides_ClassId_Date_Index",
                table: "LessonTeacherOverrides",
                columns: new[] { "ClassId", "Date", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSwapRequests_RequesterUserId",
                table: "ScheduleSwapRequests",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSwapRequests_Status_TargetDate",
                table: "ScheduleSwapRequests",
                columns: new[] { "Status", "TargetDate" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId_CreatedAt",
                table: "UserNotifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_Endpoint",
                table: "WebPushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_UserId",
                table: "WebPushSubscriptions",
                column: "UserId");

            // 一次性为已存在的内置“班主任”“老师”角色开启“老师主动换课”（RequestScheduleSwap = 4096）；
            // 放在迁移里只执行一次，之后系统管理员关闭也不会在启动时被重置。
            migrationBuilder.Sql(
                "UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | 4096 " +
                "WHERE Id IN ('44444444-4444-4444-4444-444444444444', '55555555-5555-5555-5555-555555555555');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonTeacherOverrides");

            migrationBuilder.DropTable(
                name: "ScheduleSwapRequests");

            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropTable(
                name: "WebPushSubscriptions");

            migrationBuilder.DropColumn(
                name: "VapidPrivateKey",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "VapidPublicKey",
                table: "SystemMetadata");
        }
    }
}
