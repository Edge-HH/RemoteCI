using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionAccessPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtensionPolicies",
                columns: table => new
                {
                    ExtensionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowNonAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionPolicies", x => x.ExtensionId);
                });

            migrationBuilder.CreateTable(
                name: "UserExtensionPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ShowOnWatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExtensionPreferences", x => new { x.UserId, x.ExtensionId });
                    table.ForeignKey(
                        name: "FK_UserExtensionPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 旧版的 32 同时代表主界面、音量和电源；拆分后保留原有用户的全部能力。
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "GrantedPermissions" = "GrantedPermissions" | 256
                WHERE ("GrantedPermissions" & 32) = 32;
                UPDATE "AccountRoles"
                SET "DefaultPermissions" = "DefaultPermissions" | 256
                WHERE ("DefaultPermissions" & 32) = 32;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "GrantedPermissions" = "GrantedPermissions" & ~256;
                UPDATE "AccountRoles"
                SET "DefaultPermissions" = "DefaultPermissions" & ~256;
                """);

            migrationBuilder.DropTable(
                name: "ExtensionPolicies");

            migrationBuilder.DropTable(
                name: "UserExtensionPreferences");
        }
    }
}
