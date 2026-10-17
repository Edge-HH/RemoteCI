using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace RemoteCI.Server.Data.Migrations;

public partial class AddPersonalCourses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("PersonalCourses", table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            UserId = table.Column<Guid>(type: "TEXT", nullable: false),
            Subject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
            Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
            StartsAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
            EndsAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
            TimeZoneOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
            ReminderMinutes = table.Column<int>(type: "INTEGER", nullable: true),
            NotifyAtStart = table.Column<bool>(type: "INTEGER", nullable: false),
            UpcomingSent = table.Column<bool>(type: "INTEGER", nullable: false),
            StartSent = table.Column<bool>(type: "INTEGER", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_PersonalCourses", x => x.Id);
            table.ForeignKey("FK_PersonalCourses_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_PersonalCourses_UserId_StartsAtUnixMs", "PersonalCourses", new[] { "UserId", "StartsAtUnixMs" });
        migrationBuilder.CreateIndex("IX_PersonalCourses_EndsAtUnixMs", "PersonalCourses", "EndsAtUnixMs");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PersonalCourses");
}
