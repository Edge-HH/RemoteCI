using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationApprovals",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationApprovals", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_OperationApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationApprovals_ExpiresAtUnixMs",
                table: "OperationApprovals",
                column: "ExpiresAtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_OperationApprovals_UserId",
                table: "OperationApprovals",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationApprovals");
        }
    }
}
