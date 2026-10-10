using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearLegacyDefaultClassReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 与上一个迁移分开：SQLite 的列可空调整需要重建表，数据更新必须在重建完成后执行。
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

        }
    }
}
