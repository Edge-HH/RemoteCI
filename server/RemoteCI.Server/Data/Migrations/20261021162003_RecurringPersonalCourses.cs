using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// 自定义课程从单次起止时间改为“每周星期几 + 当天时刻 + 轮换周”。已有课程按原日期保留为单次课程（Date），
    /// 已发送的提醒标记换成那次课的开始时刻；同时新增全局学期开始日期。
    /// </summary>
    public partial class RecurringPersonalCourses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PersonalCourses_EndsAtUnixMs",
                table: "PersonalCourses");

            migrationBuilder.DropIndex(
                name: "IX_PersonalCourses_UserId_StartsAtUnixMs",
                table: "PersonalCourses");

            migrationBuilder.AddColumn<DateOnly>(
                name: "SemesterStartDate",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SemesterSyncToClassrooms",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(name: "WeekDay", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "StartMinutes", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "EndMinutes", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "WeekCountDiv", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "WeekCountDivTotal", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 2);
            migrationBuilder.AddColumn<DateOnly>(name: "Date", table: "PersonalCourses", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<long>(name: "UpcomingSentFor", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "StartSentFor", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0L);

            // 按课程自己的 UTC 偏移换算本地日期与时刻；跨过午夜的旧课程截到当天 23:59。
            migrationBuilder.Sql("""
                UPDATE PersonalCourses SET
                    Date = date(StartsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch'),
                    WeekDay = CAST(strftime('%w', StartsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') AS INTEGER),
                    StartMinutes = CAST(strftime('%H', StartsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') AS INTEGER) * 60
                        + CAST(strftime('%M', StartsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') AS INTEGER),
                    EndMinutes = CASE
                        WHEN date(EndsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') = date(StartsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch')
                        THEN CAST(strftime('%H', EndsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') AS INTEGER) * 60
                            + CAST(strftime('%M', EndsAtUnixMs / 1000 + TimeZoneOffsetMinutes * 60, 'unixepoch') AS INTEGER)
                        ELSE 1439 END,
                    UpcomingSentFor = CASE WHEN UpcomingSent THEN StartsAtUnixMs ELSE 0 END,
                    StartSentFor = CASE WHEN StartSent THEN StartsAtUnixMs ELSE 0 END;
                UPDATE PersonalCourses SET StartMinutes = EndMinutes - 1 WHERE EndMinutes <= StartMinutes;
                """);

            migrationBuilder.DropColumn(name: "StartsAtUnixMs", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "EndsAtUnixMs", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "UpcomingSent", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "StartSent", table: "PersonalCourses");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalCourses_UserId",
                table: "PersonalCourses",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PersonalCourses_UserId",
                table: "PersonalCourses");

            migrationBuilder.AddColumn<long>(name: "StartsAtUnixMs", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "EndsAtUnixMs", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<bool>(name: "UpcomingSent", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "StartSent", table: "PersonalCourses", type: "INTEGER", nullable: false, defaultValue: false);

            // 每周课程回退为下一次（今天或之后最近的那个星期几）的单次课程，旧版本不认识轮换。
            migrationBuilder.Sql("""
                UPDATE PersonalCourses SET Date = date('now', '+' || ((WeekDay - CAST(strftime('%w', 'now') AS INTEGER) + 7) % 7) || ' days') WHERE Date IS NULL;
                UPDATE PersonalCourses SET
                    StartsAtUnixMs = (CAST(strftime('%s', Date) AS INTEGER) + StartMinutes * 60 - TimeZoneOffsetMinutes * 60) * 1000,
                    EndsAtUnixMs = (CAST(strftime('%s', Date) AS INTEGER) + EndMinutes * 60 - TimeZoneOffsetMinutes * 60) * 1000;
                UPDATE PersonalCourses SET UpcomingSent = UpcomingSentFor = StartsAtUnixMs, StartSent = StartSentFor = StartsAtUnixMs;
                """);

            migrationBuilder.DropColumn(name: "SemesterStartDate", table: "SystemMetadata");
            migrationBuilder.DropColumn(name: "SemesterSyncToClassrooms", table: "SystemMetadata");
            migrationBuilder.DropColumn(name: "WeekDay", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "StartMinutes", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "EndMinutes", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "WeekCountDiv", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "WeekCountDivTotal", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "Date", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "UpcomingSentFor", table: "PersonalCourses");
            migrationBuilder.DropColumn(name: "StartSentFor", table: "PersonalCourses");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalCourses_EndsAtUnixMs",
                table: "PersonalCourses",
                column: "EndsAtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalCourses_UserId_StartsAtUnixMs",
                table: "PersonalCourses",
                columns: new[] { "UserId", "StartsAtUnixMs" });
        }
    }
}
