using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassPairingCodesAndUnassignedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPersistent",
                table: "PluginPairingCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "PluginPairingCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Assigned",
                table: "PluginCredentials",
                type: "INTEGER",
                nullable: false,
                // 既有凭据都是按班级配对的设备，升级后必须保持“已分配”，否则会全部掉进“未分配”。
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassNameRemark",
                table: "PluginCredentials",
                type: "TEXT",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPersistent",
                table: "PluginPairingCodes");

            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "PluginPairingCodes");

            migrationBuilder.DropColumn(
                name: "Assigned",
                table: "PluginCredentials");

            migrationBuilder.DropColumn(
                name: "ClassNameRemark",
                table: "PluginCredentials");
        }
    }
}
