using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDefaultClassAddSystemOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LegacyDefaultClassMigrated",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "ClassroomId",
                table: "PluginPairingCodes",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClassroomId",
                table: "PluginCredentials",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemOwner",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 未分配设备与统一连接码不再借用旧版默认班级 Id。
            migrationBuilder.Sql("UPDATE PluginCredentials SET ClassroomId = NULL WHERE Assigned = 0;");
            migrationBuilder.Sql("UPDATE PluginPairingCodes SET ClassroomId = NULL WHERE IsShared = 1;");

            // 全新数据库（还没有任何账号）不保留早期迁移 AddClassrooms 插入的“默认班级”，
            // 首次进入 WebUI 时由系统管理员手动新建第一个班级；已有部署的遗留默认班级由启动时的一次性升级处理。
            migrationBuilder.Sql("""
                DELETE FROM Classrooms
                WHERE Id = '33333333-3333-3333-3333-333333333333'
                  AND NOT EXISTS (SELECT 1 FROM AspNetUsers);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LegacyDefaultClassMigrated",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "IsSystemOwner",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClassroomId",
                table: "PluginPairingCodes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ClassroomId",
                table: "PluginCredentials",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
