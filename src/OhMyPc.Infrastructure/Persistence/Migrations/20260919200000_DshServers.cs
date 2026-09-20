using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260919200000_DshServers")]
public partial class DshServers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DshServers",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Host = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SshPort = table.Column<int>(type: "INTEGER", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AuthKind = table.Column<int>(type: "INTEGER", nullable: false),
                KeyPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                RemotePort = table.Column<int>(type: "INTEGER", nullable: false),
                LocalPort = table.Column<int>(type: "INTEGER", nullable: false),
                HostKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                ConfigSyncSelection = table.Column<string>(type: "TEXT", nullable: true),
                EncryptedPassword = table.Column<byte[]>(type: "BLOB", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DshServers", x => x.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "DshServers");
}
