using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassGroups", x => x.Id);
                });

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Classrooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_GroupId",
                table: "Classrooms",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Classrooms_ClassGroups_GroupId",
                table: "Classrooms",
                column: "GroupId",
                principalTable: "ClassGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Classrooms_ClassGroups_GroupId",
                table: "Classrooms");

            migrationBuilder.DropIndex(
                name: "IX_Classrooms_GroupId",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Classrooms");

            migrationBuilder.DropTable(
                name: "ClassGroups");
        }
    }
}
