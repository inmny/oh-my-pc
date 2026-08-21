using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260811100000_VpnDailyUsage")]
public partial class VpnDailyUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VpnDailyUsage",
            columns: table => new
            {
                Date = table.Column<string>(type: "TEXT", nullable: false),
                UploadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TransferLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                ObservedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_VpnDailyUsage", x => x.Date));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "VpnDailyUsage");
}
