using Microsoft.EntityFrameworkCore.Migrations;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <summary>
    /// 权限细化：账号的个人权限改为相对角色的“新增 + 收回”，新增系统管理权限；
    /// 原先合并在其他权限里或写死给班主任的功能拆成独立权限，已拥有原权限的角色与账号补齐，升级后行为不变。
    /// 数据换算与 <see cref="PermissionUpgrade"/>（导入旧备份时使用）一一对应。
    /// </summary>
    public partial class RefinePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GrantedManagementPermissions",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RevokedManagementPermissions",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RevokedPermissions",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultManagementPermissions",
                table: "AccountRoles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // 1. 拆分原有权限：角色默认权限与账号个人新增权限按同一规则补齐。
            foreach (var (table, column) in new[] { ("AccountRoles", "DefaultPermissions"), ("AspNetUsers", "GrantedPermissions") })
            {
                Split(migrationBuilder, table, column, UserPermissions.SendNotifications, UserPermissions.ClearNotifications);
                Split(migrationBuilder, table, column, UserPermissions.PowerControl, UserPermissions.VolumeControl);
                Split(migrationBuilder, table, column, UserPermissions.MainMenuControl, UserPermissions.MainWindowLayout);
                Split(migrationBuilder, table, column, UserPermissions.ManageUsers, PermissionUpgrade.SplitFromManageUsers);
            }

            // 2. 原“人员管理”顺带可进入的系统设置页（登录与访问、插件与课表同步）改为系统管理权限。
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultManagementPermissions = DefaultManagementPermissions | {(int)PermissionUpgrade.ManagementFromManageUsers}
                WHERE (DefaultPermissions & {(int)UserPermissions.ManageUsers}) <> 0 AND Kind <> 2;
                """);
            migrationBuilder.Sql($"""
                UPDATE AspNetUsers SET GrantedManagementPermissions = GrantedManagementPermissions | {(int)PermissionUpgrade.ManagementFromManageUsers}
                WHERE (GrantedPermissions & {(int)UserPermissions.ManageUsers}) <> 0 AND Role <> 2;
                """);

            // 3. 内置“班主任”（Kind = 4）：原先按班主任身份开放的功能与“班级自治策略”开关换算成该角色的默认权限。
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | {(int)PermissionUpgrade.ClassHeadFeatures} WHERE Kind = 4;
                """);
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | {(int)UserPermissions.ConfigureExtensions}
                WHERE Kind = 4 AND (DefaultPermissions & {(int)UserPermissions.RunExtensions}) <> 0;
                """);
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | {(int)UserPermissions.ManageClassProfile}
                WHERE Kind = 4 AND (DefaultPermissions & {(int)UserPermissions.ManageSchedule}) <> 0;
                """);
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | {(int)UserPermissions.ManageClassInfo}
                WHERE Kind = 4 AND EXISTS (SELECT 1 FROM SystemMetadata WHERE Id = 1 AND (ClassAdminCanRename <> 0 OR ClassAdminCanChangeAvatar <> 0));
                """);
            migrationBuilder.Sql($"""
                UPDATE AccountRoles SET DefaultPermissions = DefaultPermissions | {(int)UserPermissions.PullSchedule}
                WHERE Kind = 4 AND EXISTS (SELECT 1 FROM SystemMetadata WHERE Id = 1 AND ClassAdminCanPullSchedule <> 0);
                """);

            // 4. 授权镜像版本前进，让在线插件重新拉取账号与权限。
            migrationBuilder.Sql("UPDATE SystemMetadata SET AccountVersion = AccountVersion + 1 WHERE Id = 1;");
        }

        private static void Split(
            MigrationBuilder migrationBuilder, string table, string column, UserPermissions source, UserPermissions added) =>
            migrationBuilder.Sql(
                $"UPDATE {table} SET {column} = {column} | {(int)added} WHERE ({column} & {(int)source}) <> 0;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrantedManagementPermissions",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RevokedManagementPermissions",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RevokedPermissions",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DefaultManagementPermissions",
                table: "AccountRoles");
        }
    }
}
