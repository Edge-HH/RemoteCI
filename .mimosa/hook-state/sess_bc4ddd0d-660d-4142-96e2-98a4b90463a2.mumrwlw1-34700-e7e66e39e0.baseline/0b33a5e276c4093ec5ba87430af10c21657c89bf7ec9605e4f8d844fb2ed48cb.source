using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginSoftwareInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SoftwareInventoryAt",
                table: "PluginCredentials",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SoftwareInventoryJson",
                table: "PluginCredentials",
                type: "TEXT",
                maxLength: 65536,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftwareInventoryAt",
                table: "PluginCredentials");

            migrationBuilder.DropColumn(
                name: "SoftwareInventoryJson",
                table: "PluginCredentials");
        }
    }
}
