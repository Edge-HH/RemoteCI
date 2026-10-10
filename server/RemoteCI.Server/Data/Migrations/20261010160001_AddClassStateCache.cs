using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassStateCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassStateCaches",
                columns: table => new
                {
                    ClassroomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExtensionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExtensionGroupsJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassStateCaches", x => x.ClassroomId);
                    table.ForeignKey(
                        name: "FK_ClassStateCaches_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassStateCaches");
        }
    }
}
