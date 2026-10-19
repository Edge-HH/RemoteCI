using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProfileDriftCheckEnabled",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileDriftCheckTime",
                table: "SystemMetadata",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "12:30");

            migrationBuilder.AddColumn<string>(
                name: "ProfileDriftLastCheckDate",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProfileDriftReports",
                columns: table => new
                {
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CheckedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfileRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileDriftReports", x => x.ClassId);
                });

            migrationBuilder.CreateTable(
                name: "StoredProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    SavedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SavedByName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SavedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoredProfileVersions_StoredProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "StoredProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredProfileVersions_ProfileId_Revision",
                table: "StoredProfileVersions",
                columns: new[] { "ProfileId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileDriftReports");

            migrationBuilder.DropTable(
                name: "StoredProfileVersions");

            migrationBuilder.DropColumn(
                name: "ProfileDriftCheckEnabled",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ProfileDriftCheckTime",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ProfileDriftLastCheckDate",
                table: "SystemMetadata");
        }
    }
}
