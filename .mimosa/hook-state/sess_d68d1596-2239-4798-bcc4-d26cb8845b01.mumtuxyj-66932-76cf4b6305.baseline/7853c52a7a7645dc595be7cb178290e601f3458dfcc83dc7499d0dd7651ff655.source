using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassSystemV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 首登设置密码：导入时可以不设密码，首次登录强制补设。
            migrationBuilder.AddColumn<bool>(
                name: "PasswordPending",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SetupTokenHash",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupTokenExpiresAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // 分组层级：父子自引用，删除父分组时子分组回到根。
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "ClassGroups",
                type: "TEXT",
                nullable: true);

            // 班级头像：小尺寸图片直接存库，经匿名接口对外提供。
            migrationBuilder.AddColumn<byte[]>(
                name: "Avatar",
                table: "Classrooms",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarContentType",
                table: "Classrooms",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvatarUpdatedAt",
                table: "Classrooms",
                type: "TEXT",
                nullable: true);

            // 班级与分组改为多对多：先把单分组归属迁到新表，再移除旧列。
            migrationBuilder.CreateTable(
                name: "ClassGroupAssignments",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassGroupAssignments", x => new { x.GroupId, x.ClassroomId });
                    table.ForeignKey(
                        name: "FK_ClassGroupAssignments_ClassGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ClassGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassGroupAssignments_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                "INSERT OR IGNORE INTO ClassGroupAssignments (GroupId, ClassroomId) " +
                "SELECT GroupId, Id FROM Classrooms WHERE GroupId IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_ClassGroups_ParentId",
                table: "ClassGroups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassGroupAssignments_ClassroomId",
                table: "ClassGroupAssignments",
                column: "ClassroomId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClassGroups_ClassGroups_ParentId",
                table: "ClassGroups",
                column: "ParentId",
                principalTable: "ClassGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropForeignKey(
                name: "FK_Classrooms_ClassGroups_GroupId",
                table: "Classrooms");

            migrationBuilder.DropIndex(
                name: "IX_Classrooms_GroupId",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Classrooms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回滚到单分组：每班取一个分组（按分配顺序）；多于一个分组的班级会丢失其余归属。
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Classrooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE Classrooms SET GroupId = COALESCE((" +
                "SELECT a.GroupId FROM ClassGroupAssignments a WHERE a.ClassroomId = Classrooms.Id LIMIT 1), NULL);");

            migrationBuilder.DropForeignKey(
                name: "FK_ClassGroups_ClassGroups_ParentId",
                table: "ClassGroups");

            migrationBuilder.DropTable(
                name: "ClassGroupAssignments");

            migrationBuilder.DropIndex(
                name: "IX_ClassGroups_ParentId",
                table: "ClassGroups");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "ClassGroups");

            migrationBuilder.DropColumn(
                name: "PasswordPending",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SetupTokenHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SetupTokenExpiresAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Avatar",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "AvatarContentType",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "AvatarUpdatedAt",
                table: "Classrooms");

            migrationBuilder.AddForeignKey(
                name: "FK_Classrooms_ClassGroups_GroupId",
                table: "Classrooms",
                column: "GroupId",
                principalTable: "ClassGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
