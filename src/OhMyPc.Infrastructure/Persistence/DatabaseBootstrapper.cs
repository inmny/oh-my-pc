using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(IDbContextFactory<AppDbContext> contextFactory)
{
    private const string InputStatusSeedKey = "seed.input-status-rule.v1";
    private const string VpnRulesSeedKey = "seed.vpn-notification-rules.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);

        if (!await db.Settings.AnyAsync(x => x.Key == "app", cancellationToken))
        {
            db.Settings.Add(new SettingEntity
            {
                Key = "app",
                JsonValue = JsonSerializer.Serialize(new AppSettings(), JsonOptions),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        if (!await db.AutomationRules.AnyAsync(cancellationToken))
        {
            db.AutomationRules.AddRange(
                ToEntity(QuotaRule(
                    "quota-warning",
                    "Quota below 20%",
                    20,
                    NotificationChannels.Danmaku | NotificationChannels.Tray,
                    NotificationSeverity.Warning)),
                ToEntity(QuotaRule(
                    "quota-critical",
                    "Quota below 10%",
                    10,
                    NotificationChannels.System | NotificationChannels.Tray,
                    NotificationSeverity.Critical)),
                ToEntity(new AutomationRuleDefinition
                {
                    Id = "provider-status",
                    Name = "Provider status changed",
                    EventType = AutomationEventTypes.ProviderStatusChanged,
                    Actions = [LocalAction(NotificationChannels.Danmaku | NotificationChannels.Tray, NotificationSeverity.Info)],
                    CooldownMinutes = 0,
                    RespectQuietHours = true
                }));
        }

        if (!await db.Settings.AnyAsync(x => x.Key == InputStatusSeedKey, cancellationToken))
        {
            if (!await db.AutomationRules.AnyAsync(x => x.Id == "input-model-gpt-5.6-sol", cancellationToken))
            {
                db.AutomationRules.Add(ToEntity(new AutomationRuleDefinition
                {
                    Id = "input-model-gpt-5.6-sol",
                    Name = "GPT-5.6 Sol availability changed",
                    EventType = AutomationEventTypes.InputModelAvailabilityChanged,
                    Conditions =
                    [
                        new AutomationConditionDefinition
                        {
                            Field = "model",
                            Operator = AutomationConditionOperator.Equal,
                            ValueKind = AutomationValueKind.Text,
                            Value = JsonValue.Create("gpt-5.6-sol")
                        }
                    ],
                    Actions = [LocalAction(NotificationChannels.Danmaku | NotificationChannels.Tray, NotificationSeverity.Info)],
                    CooldownMinutes = 0,
                    RespectQuietHours = true
                }));
            }

            db.Settings.Add(new SettingEntity
            {
                Key = InputStatusSeedKey,
                JsonValue = "true",
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        if (!await db.Settings.AnyAsync(x => x.Key == VpnRulesSeedKey, cancellationToken))
        {
            var vpnRules = VpnRules();
            foreach (var rule in vpnRules)
            {
                if (!await db.AutomationRules.AnyAsync(x => x.Id == rule.Id, cancellationToken))
                {
                    db.AutomationRules.Add(ToEntity(rule));
                }
            }

            db.Settings.Add(new SettingEntity
            {
                Key = VpnRulesSeedKey,
                JsonValue = "true",
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static AutomationRuleDefinition QuotaRule(
        string id,
        string name,
        double threshold,
        NotificationChannels channels,
        NotificationSeverity severity) => new()
    {
        Id = id,
        Name = name,
        EventType = AutomationEventTypes.QuotaObserved,
        Conditions =
        [
            new AutomationConditionDefinition
            {
                Field = "remainingPercent",
                Operator = AutomationConditionOperator.LessThanOrEqual,
                ValueKind = AutomationValueKind.Number,
                Value = JsonValue.Create(threshold)
            }
        ],
        Actions = [LocalAction(channels, severity)],
        CooldownMinutes = 240,
        RespectQuietHours = true
    };

    private static IReadOnlyList<AutomationRuleDefinition> VpnRules() =>
    [
        new AutomationRuleDefinition
        {
            Id = "vpn-quota-warning",
            Name = "VPN traffic below 20%",
            EventType = AutomationEventTypes.VpnQuotaObserved,
            Conditions =
            [
                NumberCondition("remainingPercent", AutomationConditionOperator.GreaterThan, 10),
                NumberCondition("remainingPercent", AutomationConditionOperator.LessThanOrEqual, 20)
            ],
            Actions = [LocalAction(NotificationChannels.Danmaku | NotificationChannels.Tray, NotificationSeverity.Warning)],
            CooldownMinutes = 240,
            RespectQuietHours = true
        },
        new AutomationRuleDefinition
        {
            Id = "vpn-quota-critical",
            Name = "VPN traffic below 10%",
            EventType = AutomationEventTypes.VpnQuotaObserved,
            Conditions = [NumberCondition("remainingPercent", AutomationConditionOperator.LessThanOrEqual, 10)],
            Actions = [LocalAction(NotificationChannels.System | NotificationChannels.Tray, NotificationSeverity.Critical)],
            CooldownMinutes = 240,
            RespectQuietHours = true
        },
        new AutomationRuleDefinition
        {
            Id = "vpn-expiration-warning",
            Name = "VPN plan expires within 7 days",
            EventType = AutomationEventTypes.VpnExpirationObserved,
            Conditions = [NumberCondition("daysRemaining", AutomationConditionOperator.LessThanOrEqual, 7)],
            Actions = [LocalAction(NotificationChannels.Danmaku | NotificationChannels.Tray, NotificationSeverity.Warning)],
            CooldownMinutes = 1440,
            RespectQuietHours = true
        },
        new AutomationRuleDefinition
        {
            Id = "vpn-authentication-failed",
            Name = "VPN login expired",
            EventType = AutomationEventTypes.VpnStatusChanged,
            Conditions =
            [
                new AutomationConditionDefinition
                {
                    Field = "authenticationFailed",
                    Operator = AutomationConditionOperator.Equal,
                    ValueKind = AutomationValueKind.Boolean,
                    Value = JsonValue.Create(true)
                }
            ],
            Actions = [LocalAction(NotificationChannels.System | NotificationChannels.Tray, NotificationSeverity.Critical)],
            CooldownMinutes = 0,
            RespectQuietHours = false
        }
    ];

    private static AutomationConditionDefinition NumberCondition(
        string field,
        AutomationConditionOperator @operator,
        double value) => new()
    {
        Field = field,
        Operator = @operator,
        ValueKind = AutomationValueKind.Number,
        Value = JsonValue.Create(value)
    };

    private static AutomationActionDefinition LocalAction(NotificationChannels channels, NotificationSeverity severity) =>
        new LocalNotificationActionOptions { Channels = channels, Severity = severity }.ToDefinition();

    private static AutomationRuleEntity ToEntity(AutomationRuleDefinition rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        EventType = rule.EventType,
        MatchMode = (int)rule.MatchMode,
        ConditionsJson = JsonSerializer.Serialize(rule.Conditions, JsonOptions),
        ActionsJson = JsonSerializer.Serialize(rule.Actions, JsonOptions),
        CooldownMinutes = rule.CooldownMinutes,
        RespectQuietHours = rule.RespectQuietHours
    };
}
