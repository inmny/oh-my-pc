using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260816180000_NotificationHistory")]
public partial class NotificationHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Notifications",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Origin = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Body = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Channels = table.Column<int>(type: "INTEGER", nullable: false),
                Severity = table.Column<int>(type: "INTEGER", nullable: false),
                SubjectKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Notifications", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_CreatedAt_Id",
            table: "Notifications",
            columns: new[] { "CreatedAt", "Id" },
            descending: new[] { true, true });

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_Severity_CreatedAt_Id",
            table: "Notifications",
            columns: new[] { "Severity", "CreatedAt", "Id" },
            descending: new[] { false, true, true });

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_Source_CreatedAt_Id",
            table: "Notifications",
            columns: new[] { "Source", "CreatedAt", "Id" },
            descending: new[] { false, true, true });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Notifications");
}
