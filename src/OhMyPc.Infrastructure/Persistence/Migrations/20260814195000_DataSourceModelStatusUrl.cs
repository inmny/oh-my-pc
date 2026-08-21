using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260814195000_DataSourceModelStatusUrl")]
public partial class DataSourceModelStatusUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ModelStatusUrl",
            table: "DataSources",
            type: "TEXT",
            maxLength: 1024,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql("DELETE FROM AutomationSourceStates WHERE Key LIKE 'input-status:model:%';");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "ModelStatusUrl",
            table: "DataSources");
}
