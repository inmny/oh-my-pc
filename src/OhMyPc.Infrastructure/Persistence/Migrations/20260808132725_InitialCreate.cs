using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyUsage",
                columns: table => new
                {
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Client = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CostMicroUsd = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyUsage", x => new { x.Date, x.DeviceId, x.Client, x.Provider, x.Model });
                });

            migrationBuilder.CreateTable(
                name: "DataSources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerKind = table.Column<int>(type: "INTEGER", nullable: false),
                    Operator = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: true),
                    WindowKey = table.Column<string>(type: "TEXT", nullable: true),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    MatchText = table.Column<string>(type: "TEXT", nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RespectQuietHours = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationStates",
                columns: table => new
                {
                    RuleId = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", nullable: false),
                    LastMatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastNumericValue = table.Column<double>(type: "REAL", nullable: true),
                    LastTextValue = table.Column<string>(type: "TEXT", nullable: true),
                    LastNotifiedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationStates", x => new { x.RuleId, x.SubjectKey });
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    JsonValue = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK_Credentials_DataSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurrentQuotas",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    WindowKey = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Used = table.Column<double>(type: "REAL", nullable: false),
                    Limit = table.Column<double>(type: "REAL", nullable: true),
                    Remaining = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    ResetAt = table.Column<string>(type: "TEXT", nullable: true),
                    ObservedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentQuotas", x => new { x.SourceId, x.WindowKey });
                    table.ForeignKey(
                        name: "FK_CurrentQuotas_DataSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyUsage_Date_Client",
                table: "DailyUsage",
                columns: new[] { "Date", "Client" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "CurrentQuotas");

            migrationBuilder.DropTable(
                name: "DailyUsage");

            migrationBuilder.DropTable(
                name: "NotificationRules");

            migrationBuilder.DropTable(
                name: "NotificationStates");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "DataSources");
        }
    }
}
