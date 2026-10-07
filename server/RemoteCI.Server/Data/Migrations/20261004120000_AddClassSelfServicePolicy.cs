using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassSelfServicePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 改名、头像与拉取课表默认保持升级前“班主任可用”的行为；扩展设置是新能力，默认只由系统管理员修改。
            migrationBuilder.AddColumn<bool>(
                name: "ClassAdminCanChangeAvatar",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClassAdminCanEditExtensionSettings",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ClassAdminCanPullSchedule",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClassAdminCanRename",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassAdminCanChangeAvatar",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ClassAdminCanEditExtensionSettings",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ClassAdminCanPullSchedule",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "ClassAdminCanRename",
                table: "SystemMetadata");
        }
    }
}
