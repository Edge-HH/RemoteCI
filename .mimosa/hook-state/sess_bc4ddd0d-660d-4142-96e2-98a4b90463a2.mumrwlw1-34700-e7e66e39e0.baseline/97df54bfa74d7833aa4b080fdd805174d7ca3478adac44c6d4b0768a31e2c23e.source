using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoleDefinitionId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "AccountRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultPermissions = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Cadence = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeOfDay = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxBackups = table.Column<int>(type: "INTEGER", nullable: false),
                    LastScheduledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSucceededAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupConfigurations", x => x.Id);
                });

            migrationBuilder.Sql("""
                INSERT INTO AccountRoles (Id, Name, NormalizedName, Kind, DefaultPermissions, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', 'Student', 'STUDENT', 1, 0, '2026-08-17T00:00:00+00:00', '2026-08-17T00:00:00+00:00');
                INSERT INTO AccountRoles (Id, Name, NormalizedName, Kind, DefaultPermissions, CreatedAt, UpdatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', 'Administrator', 'ADMINISTRATOR', 2, 127, '2026-08-17T00:00:00+00:00', '2026-08-17T00:00:00+00:00');
                UPDATE AspNetUsers SET RoleDefinitionId = CASE WHEN Role = 2 THEN '22222222-2222-2222-2222-222222222222' ELSE '11111111-1111-1111-1111-111111111111' END;
                INSERT INTO BackupConfigurations (Id, Enabled, Cadence, TimeOfDay, DayOfWeek, MaxBackups) VALUES (1, 1, 2, '02:00:00', 1, 7);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_RoleDefinitionId",
                table: "AspNetUsers",
                column: "RoleDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRoles_NormalizedName",
                table: "AccountRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_AccountRoles_RoleDefinitionId",
                table: "AspNetUsers",
                column: "RoleDefinitionId",
                principalTable: "AccountRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_AccountRoles_RoleDefinitionId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "AccountRoles");

            migrationBuilder.DropTable(
                name: "BackupConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_RoleDefinitionId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RoleDefinitionId",
                table: "AspNetUsers");
        }
    }
}
