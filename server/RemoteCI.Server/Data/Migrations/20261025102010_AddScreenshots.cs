using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ScreenshotNotifyClassroom",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 一次性为已存在的内置“班主任”角色开启“屏幕截图”（CaptureScreen = 32768）；
            // 放在迁移里只执行一次，之后系统管理员关闭也不会在启动时被重置。
            migrationBuilder.Sql(
                "UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | 32768 " +
                "WHERE Id = '44444444-4444-4444-4444-444444444444';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScreenshotNotifyClassroom",
                table: "SystemMetadata");
        }
    }
}
