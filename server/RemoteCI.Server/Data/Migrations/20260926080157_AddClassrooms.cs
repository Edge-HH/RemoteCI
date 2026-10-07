using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassrooms : Migration
    {
        private const string DefaultClassId = "33333333-3333-3333-3333-333333333333";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Classrooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    VisitorAccessEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classrooms", x => x.Id);
                });

            // 升级自单班级版本：默认班级承接原全局访客开关，所有存量数据都归属它。
            migrationBuilder.Sql($@"
                INSERT INTO Classrooms (Id, Name, VisitorAccessEnabled, CreatedAt, UpdatedAt)
                SELECT '{DefaultClassId}', '默认班级',
                       COALESCE((SELECT VisitorAccessEnabled FROM SystemMetadata WHERE Id = 1), 0),
                       '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00'
                WHERE NOT EXISTS (SELECT 1 FROM Classrooms WHERE Id = '{DefaultClassId}');");

            migrationBuilder.AddColumn<Guid>(
                name: "ClassroomId",
                table: "PluginCredentials",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid(DefaultClassId));

            migrationBuilder.AddColumn<Guid>(
                name: "ClassroomId",
                table: "PluginPairingCodes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid(DefaultClassId));

            migrationBuilder.CreateTable(
                name: "ClassMemberships",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassMemberships", x => new { x.UserId, x.ClassroomId });
                    table.ForeignKey(
                        name: "FK_ClassMemberships_AccountRoles_RoleDefinitionId",
                        column: x => x.RoleDefinitionId,
                        principalTable: "AccountRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassMemberships_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 存量用户按其当前角色在默认班级建立成员关系，升级前权限保持不变。
            migrationBuilder.Sql($@"
                INSERT INTO ClassMemberships (UserId, ClassroomId, RoleDefinitionId)
                SELECT Id, '{DefaultClassId}', RoleDefinitionId
                FROM AspNetUsers
                WHERE NOT EXISTS (
                    SELECT 1 FROM ClassMemberships WHERE ClassMemberships.UserId = AspNetUsers.Id);");

            migrationBuilder.CreateIndex(
                name: "IX_PluginCredentials_ClassroomId",
                table: "PluginCredentials",
                column: "ClassroomId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassMemberships_ClassroomId",
                table: "ClassMemberships",
                column: "ClassroomId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassMemberships_RoleDefinitionId",
                table: "ClassMemberships",
                column: "RoleDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_Name",
                table: "Classrooms",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PluginCredentials_Classrooms_ClassroomId",
                table: "PluginCredentials",
                column: "ClassroomId",
                principalTable: "Classrooms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // 删除全局访客开关。SQLite 上 EF 的 DropColumn 会整表重建并丢失其余列的默认值，
            // 而 Bootstrap 启动引导依赖这些默认值；这里改为显式重建并保留全部默认值。
            migrationBuilder.Sql("""
                CREATE TABLE "SystemMetadata_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SystemMetadata" PRIMARY KEY AUTOINCREMENT,
                    "AccountVersion" INTEGER NOT NULL DEFAULT 0,
                    "ForceSenderInTitle" INTEGER NOT NULL DEFAULT 1,
                    "SchedulePullIntervalMinutes" INTEGER NOT NULL DEFAULT 0,
                    "AutoEnterVisitorPage" INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO "SystemMetadata_new" ("Id", "AccountVersion", "ForceSenderInTitle", "SchedulePullIntervalMinutes", "AutoEnterVisitorPage")
                SELECT "Id", "AccountVersion", "ForceSenderInTitle", "SchedulePullIntervalMinutes", "AutoEnterVisitorPage" FROM "SystemMetadata";
                DROP TABLE "SystemMetadata";
                ALTER TABLE "SystemMetadata_new" RENAME TO "SystemMetadata";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 还原全局访客开关列；旧 schema 的列默认值随表重建一并恢复。
            migrationBuilder.Sql("""
                CREATE TABLE "SystemMetadata_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SystemMetadata" PRIMARY KEY AUTOINCREMENT,
                    "AccountVersion" INTEGER NOT NULL DEFAULT 0,
                    "ForceSenderInTitle" INTEGER NOT NULL DEFAULT 1,
                    "SchedulePullIntervalMinutes" INTEGER NOT NULL DEFAULT 0,
                    "VisitorAccessEnabled" INTEGER NOT NULL DEFAULT 0,
                    "AutoEnterVisitorPage" INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO "SystemMetadata_new" ("Id", "AccountVersion", "ForceSenderInTitle", "SchedulePullIntervalMinutes", "VisitorAccessEnabled", "AutoEnterVisitorPage")
                SELECT "Id", "AccountVersion", "ForceSenderInTitle", "SchedulePullIntervalMinutes", 0, "AutoEnterVisitorPage" FROM "SystemMetadata";
                DROP TABLE "SystemMetadata";
                ALTER TABLE "SystemMetadata_new" RENAME TO "SystemMetadata";
                """);

            migrationBuilder.Sql($@"
                UPDATE SystemMetadata
                SET VisitorAccessEnabled = COALESCE((
                    SELECT VisitorAccessEnabled FROM Classrooms WHERE Id = '{DefaultClassId}'), 0)
                WHERE Id = 1;");

            migrationBuilder.DropForeignKey(
                name: "FK_PluginCredentials_Classrooms_ClassroomId",
                table: "PluginCredentials");

            migrationBuilder.DropTable(
                name: "ClassMemberships");

            migrationBuilder.DropTable(
                name: "Classrooms");

            migrationBuilder.DropIndex(
                name: "IX_PluginCredentials_ClassroomId",
                table: "PluginCredentials");

            migrationBuilder.DropColumn(
                name: "ClassroomId",
                table: "PluginPairingCodes");

            migrationBuilder.DropColumn(
                name: "ClassroomId",
                table: "PluginCredentials");
        }
    }
}
