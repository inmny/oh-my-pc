using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260814160000_QuotaProgressLimit")]
public partial class QuotaProgressLimit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<double>(
            name: "ProgressLimit",
            table: "CurrentQuotas",
            type: "REAL",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "ProgressLimit",
            table: "CurrentQuotas");
}
