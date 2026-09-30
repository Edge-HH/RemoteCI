using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginPageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "LoginBackground",
                table: "SystemMetadata",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoginBackgroundContentType",
                table: "SystemMetadata",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoginBackgroundOpacity",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LoginBackgroundUpdatedAt",
                table: "SystemMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoginTheme",
                table: "SystemMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoginBackground",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "LoginBackgroundContentType",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "LoginBackgroundOpacity",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "LoginBackgroundUpdatedAt",
                table: "SystemMetadata");

            migrationBuilder.DropColumn(
                name: "LoginTheme",
                table: "SystemMetadata");
        }
    }
}
