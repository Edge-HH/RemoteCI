using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetJson = table.Column<string>(type: "TEXT", nullable: false),
                    ValidFrom = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    ValidTo = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRunAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccess = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TriggerText = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DueAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuns_AutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_DedupeKey",
                table: "AutomationRuns",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_RuleId_DueAtUnixMs",
                table: "AutomationRuns",
                columns: new[] { "RuleId", "DueAtUnixMs" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_Status_DueAtUnixMs",
                table: "AutomationRuns",
                columns: new[] { "Status", "DueAtUnixMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "AutomationRules");
        }
    }
}
