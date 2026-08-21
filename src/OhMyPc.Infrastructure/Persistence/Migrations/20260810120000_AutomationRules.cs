using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhMyPc.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260810120000_AutomationRules")]
public partial class AutomationRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AutomationRules",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                EventType = table.Column<string>(type: "TEXT", nullable: false),
                MatchMode = table.Column<int>(type: "INTEGER", nullable: false),
                ConditionsJson = table.Column<string>(type: "TEXT", nullable: false),
                ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                RespectQuietHours = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AutomationRules", x => x.Id));

        migrationBuilder.CreateTable(
            name: "AutomationSourceStates",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", nullable: false),
                ValueJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AutomationSourceStates", x => x.Key));

        migrationBuilder.CreateTable(
            name: "AutomationRuleStates",
            columns: table => new
            {
                RuleId = table.Column<string>(type: "TEXT", nullable: false),
                SubjectKey = table.Column<string>(type: "TEXT", nullable: false),
                LastExecutedAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AutomationRuleStates", x => new { x.RuleId, x.SubjectKey });
                table.ForeignKey(
                    name: "FK_AutomationRuleStates_AutomationRules_RuleId",
                    column: x => x.RuleId,
                    principalTable: "AutomationRules",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql(
            """
            INSERT INTO AutomationRules
                (Id, Name, Enabled, EventType, MatchMode, ConditionsJson, ActionsJson, CooldownMinutes, RespectQuietHours)
            SELECT
                Id,
                Name,
                Enabled,
                CASE TriggerKind
                    WHEN 0 THEN 'quota.observed'
                    WHEN 1 THEN 'quota.observed'
                    WHEN 2 THEN 'provider.status.changed'
                    WHEN 3 THEN 'usage.daily.updated'
                END,
                0,
                CASE
                    WHEN TriggerKind IN (0, 1) AND SourceId IS NOT NULL AND WindowKey IS NOT NULL THEN
                        json_array(
                            json_object('field', 'sourceId', 'operator', 2, 'valueKind', 0, 'value', SourceId),
                            json_object('field', 'windowKey', 'operator', 2, 'valueKind', 0, 'value', WindowKey),
                            json_object('field', CASE TriggerKind WHEN 0 THEN 'remainingPercent' ELSE 'remaining' END, 'operator', Operator, 'valueKind', 1, 'value', Threshold))
                    WHEN TriggerKind IN (0, 1) AND SourceId IS NOT NULL THEN
                        json_array(
                            json_object('field', 'sourceId', 'operator', 2, 'valueKind', 0, 'value', SourceId),
                            json_object('field', CASE TriggerKind WHEN 0 THEN 'remainingPercent' ELSE 'remaining' END, 'operator', Operator, 'valueKind', 1, 'value', Threshold))
                    WHEN TriggerKind IN (0, 1) AND WindowKey IS NOT NULL THEN
                        json_array(
                            json_object('field', 'windowKey', 'operator', 2, 'valueKind', 0, 'value', WindowKey),
                            json_object('field', CASE TriggerKind WHEN 0 THEN 'remainingPercent' ELSE 'remaining' END, 'operator', Operator, 'valueKind', 1, 'value', Threshold))
                    WHEN TriggerKind IN (0, 1) THEN
                        json_array(
                            json_object('field', CASE TriggerKind WHEN 0 THEN 'remainingPercent' ELSE 'remaining' END, 'operator', Operator, 'valueKind', 1, 'value', Threshold))
                    WHEN TriggerKind = 2 AND SourceId IS NOT NULL THEN
                        json_array(json_object('field', 'sourceId', 'operator', 2, 'valueKind', 0, 'value', SourceId))
                    WHEN TriggerKind = 2 THEN json_array()
                    WHEN TriggerKind = 3 THEN
                        json_array(json_object('field', 'totalTokens', 'operator', Operator, 'valueKind', 1, 'value', Threshold))
                END,
                json_array(
                    json_object(
                        'kind', 'local.notification',
                        'configuration', json_object('channels', Channels, 'severity', Severity))),
                CooldownMinutes,
                RespectQuietHours
            FROM NotificationRules;
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO AutomationRuleStates (RuleId, SubjectKey, LastExecutedAt)
            SELECT
                state.RuleId,
                CASE rule.TriggerKind
                    WHEN 0 THEN replace(state.SubjectKey, ':QuotaRemainingPercent', '')
                    WHEN 1 THEN replace(state.SubjectKey, ':QuotaRemaining', '')
                    ELSE state.SubjectKey
                END,
                state.LastNotifiedAt
            FROM NotificationStates AS state
            INNER JOIN NotificationRules AS rule ON rule.Id = state.RuleId;
            """);

        migrationBuilder.DropTable(name: "NotificationStates");
        migrationBuilder.DropTable(name: "NotificationRules");

        migrationBuilder.CreateIndex(
            name: "IX_AutomationRules_Enabled_EventType",
            table: "AutomationRules",
            columns: new[] { "Enabled", "EventType" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
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
            constraints: table => table.PrimaryKey("PK_NotificationRules", x => x.Id));

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
            constraints: table => table.PrimaryKey("PK_NotificationStates", x => new { x.RuleId, x.SubjectKey }));

        migrationBuilder.Sql(
            """
            INSERT INTO NotificationRules
                (Id, Name, Enabled, TriggerKind, Operator, SourceId, WindowKey, Threshold, MatchText, Channels, Severity, CooldownMinutes, RespectQuietHours)
            SELECT
                Id,
                Name,
                Enabled,
                CASE EventType
                    WHEN 'quota.observed' THEN CASE
                        WHEN EXISTS (SELECT 1 FROM json_each(ConditionsJson) WHERE json_extract(value, '$.field') = 'remainingPercent') THEN 0
                        ELSE 1
                    END
                    WHEN 'provider.status.changed' THEN 2
                    WHEN 'usage.daily.updated' THEN 3
                END,
                coalesce((SELECT json_extract(value, '$.operator') FROM json_each(ConditionsJson)
                    WHERE json_extract(value, '$.field') IN ('remainingPercent', 'remaining', 'totalTokens') LIMIT 1), 4),
                (SELECT json_extract(value, '$.value') FROM json_each(ConditionsJson)
                    WHERE json_extract(value, '$.field') = 'sourceId' LIMIT 1),
                (SELECT json_extract(value, '$.value') FROM json_each(ConditionsJson)
                    WHERE json_extract(value, '$.field') = 'windowKey' LIMIT 1),
                coalesce((SELECT json_extract(value, '$.value') FROM json_each(ConditionsJson)
                    WHERE json_extract(value, '$.field') IN ('remainingPercent', 'remaining', 'totalTokens') LIMIT 1), 0),
                NULL,
                json_extract(ActionsJson, '$[0].configuration.channels'),
                json_extract(ActionsJson, '$[0].configuration.severity'),
                CooldownMinutes,
                RespectQuietHours
            FROM AutomationRules
            WHERE EventType IN ('quota.observed', 'provider.status.changed', 'usage.daily.updated');
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO NotificationStates
                (RuleId, SubjectKey, LastMatched, LastNumericValue, LastTextValue, LastNotifiedAt)
            SELECT RuleId, SubjectKey, 0, NULL, NULL, LastExecutedAt
            FROM AutomationRuleStates
            WHERE RuleId IN (SELECT Id FROM NotificationRules);
            """);

        migrationBuilder.DropTable(name: "AutomationRuleStates");
        migrationBuilder.DropTable(name: "AutomationSourceStates");
        migrationBuilder.DropTable(name: "AutomationRules");
    }
}
