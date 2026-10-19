using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LessonLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartTime = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    EndTime = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    TeacherName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DefaultTeacherName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsSubstitute = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonLedgerEntries_ClassId_Date_Index",
                table: "LessonLedgerEntries",
                columns: new[] { "ClassId", "Date", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LessonLedgerEntries_Date",
                table: "LessonLedgerEntries",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonLedgerEntries");
        }
    }
}
