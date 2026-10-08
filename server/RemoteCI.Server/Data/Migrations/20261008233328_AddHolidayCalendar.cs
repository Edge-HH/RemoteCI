using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHolidayCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HolidayCalendarEnabled",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "HolidaySourceUrlTemplate",
                table: "SystemMetadata",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HolidayMakeupOverrides",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FollowWeekday = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolidayMakeupOverrides", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "HolidayYearSnapshots",
                columns: table => new
                {
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolidayYearSnapshots", x => x.Year);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HolidayMakeupOverrides");

            migrationBuilder.DropTable(
                name: "HolidayYearSnapshots");

            migrationBuilder.DropColumn(
                name: "HolidayCalendarEnabled",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "HolidaySourceUrlTemplate",
                table: "SystemMetadata");
        }
    }
}
