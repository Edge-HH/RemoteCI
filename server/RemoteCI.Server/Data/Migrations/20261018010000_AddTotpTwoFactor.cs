using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteCI.Server.Data.Migrations;

public partial class AddTotpTwoFactor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ProtectedTotpKey", table: "AspNetUsers", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<long>(name: "LastTotpTimeStep", table: "AspNetUsers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>(name: "TwoFactorRecoveryHashes", table: "AspNetUsers", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProtectedTotpKey", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "LastTotpTimeStep", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "TwoFactorRecoveryHashes", table: "AspNetUsers");
    }
}
