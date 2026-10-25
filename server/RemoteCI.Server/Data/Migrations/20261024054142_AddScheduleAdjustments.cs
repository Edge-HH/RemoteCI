using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedConflictsJson",
                table: "ScheduleSwapRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvalidReason",
                table: "ScheduleSwapRequests",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClassSyncStates",
                columns: table => new
                {
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdjustmentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AdjustmentSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    AckedAdjustmentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AckedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassSyncStates", x => x.ClassId);
                    table.ForeignKey(
                        name: "FK_ClassSyncStates_Classrooms_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    FromSubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromSubject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ToSubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToSubject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TeacherName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    SwapRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<int>(type: "INTEGER", nullable: false),
                    ConflictCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    StatusReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ClosedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleAdjustments_Classrooms_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAdjustments_ClassId_Date_Index_Status",
                table: "ScheduleAdjustments",
                columns: new[] { "ClassId", "Date", "Index", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAdjustments_Status_Date",
                table: "ScheduleAdjustments",
                columns: new[] { "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAdjustments_SwapRequestId",
                table: "ScheduleAdjustments",
                column: "SwapRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassSyncStates");

            migrationBuilder.DropTable(
                name: "ScheduleAdjustments");

            migrationBuilder.DropColumn(
                name: "AcknowledgedConflictsJson",
                table: "ScheduleSwapRequests");

            migrationBuilder.DropColumn(
                name: "InvalidReason",
                table: "ScheduleSwapRequests");
        }
    }
}
