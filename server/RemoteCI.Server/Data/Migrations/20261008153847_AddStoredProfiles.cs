using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceTemplateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoredProfiles_Classrooms_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoredProfiles_StoredProfiles_SourceTemplateId",
                        column: x => x.SourceTemplateId,
                        principalTable: "StoredProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredProfiles_ClassId",
                table: "StoredProfiles",
                column: "ClassId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredProfiles_SourceTemplateId",
                table: "StoredProfiles",
                column: "SourceTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredProfiles");
        }
    }
}
