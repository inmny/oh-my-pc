using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260810160000_VpnQuota")]
public partial class VpnQuota : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VpnAccounts",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                EncryptedAuthData = table.Column<byte[]>(type: "BLOB", nullable: false),
                PlanName = table.Column<string>(type: "TEXT", nullable: false),
                UploadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TransferLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                ResetDay = table.Column<int>(type: "INTEGER", nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                LastAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                LastSuccessAt = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_VpnAccounts", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "VpnAccounts");
}
