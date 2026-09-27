using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 内置班管理员在旧数据库中先补上 API 访问位；后续启动不再覆盖该角色默认权限，
            // 这样管理员通过“角色配置”移除或恢复 API 访问时能够持久生效。
            migrationBuilder.Sql(
                "UPDATE \"AccountRoles\" SET \"DefaultPermissions\" = \"DefaultPermissions\" | 2048 " +
                "WHERE \"Id\" = '44444444-4444-4444-4444-444444444444';");

            migrationBuilder.CreateTable(
                name: "UserApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserApiKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserApiKeys_KeyHash",
                table: "UserApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserApiKeys_UserId_RevokedAt",
                table: "UserApiKeys",
                columns: new[] { "UserId", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserApiKeys");

            migrationBuilder.Sql(
                "UPDATE \"AccountRoles\" SET \"DefaultPermissions\" = \"DefaultPermissions\" & ~2048 " +
                "WHERE \"Id\" = '44444444-4444-4444-4444-444444444444';");
        }
    }
}
