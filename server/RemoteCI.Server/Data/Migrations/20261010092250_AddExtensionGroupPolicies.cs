using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionGroupPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 全局开关 ClassAdminCanEditExtensionSettings 已由逐插件开关取代，模型不再映射它，但数据库中保留该列：
            // SQLite 删列会重建 SystemMetadata 并丢失各列默认值，启动时的 INSERT OR IGNORE 种子行会因此静默失败。
            // 旧开关不迁移到新表，升级后全部插件默认只由系统管理员统一管理。
            migrationBuilder.CreateTable(
                name: "ExtensionGroupPolicies",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AllowClassAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionGroupPolicies", x => x.GroupId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtensionGroupPolicies");
        }
    }
}
