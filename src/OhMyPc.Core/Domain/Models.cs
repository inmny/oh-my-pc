using System.Text.Json.Nodes;

namespace OhMyPc.Core.Domain;

public sealed class DataSourceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New source";
    public DataSourceKind Kind { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ModelStatusUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
    public ProviderStatus Status { get; set; } = ProviderStatus.Unknown;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
}

public sealed class QuotaSnapshot
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string WindowKey { get; set; } = "total";
    public string Label { get; set; } = "Quota";
    public double Used { get; set; }
    public double? Limit { get; set; }
    public double? ProgressLimit { get; set; }
    public double? Remaining { get; set; }
    public string Unit { get; set; } = "USD";
    public DateTimeOffset? ResetAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProviderStatus Status { get; set; } = ProviderStatus.Healthy;
    public string? Detail { get; set; }

    public double? RemainingPercent => (ProgressLimit ?? Limit) is > 0 and var progressLimit && Remaining is not null
        ? Math.Clamp(Remaining.Value / progressLimit * 100d, 0d, 100d)
        : null;
}

public sealed class UsageObservation
{
    public DateOnly Date { get; set; }
    public string DeviceId { get; set; } = "device";
    public string Client { get; set; } = "unknown";
    public string Provider { get; set; } = "unknown";
    public string Model { get; set; } = "unknown";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public long MessageCount { get; set; }
    public long ActiveTimeMs { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

public readonly record struct UsageObservationScope(DateOnly Date, string DeviceId);

public sealed class UsageTrendPoint
{
    public DateOnly Date { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    public long MessageCount { get; set; }
    public long ActiveTimeMs { get; set; }
    public decimal CostUsd { get; set; }
}

public sealed class UsageBreakdownPoint
{
    public string Name { get; set; } = "";
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public decimal CostUsd { get; set; }
}

public static class AutomationEventTypes
{
    public const string QuotaObserved = "quota.observed";
    public const string ProviderStatusChanged = "provider.status.changed";
    public const string DailyUsageUpdated = "usage.daily.updated";
    public const string InputModelAvailabilityChanged = "input.model.availability.changed";
    public const string VpnQuotaObserved = "vpn.quota.observed";
    public const string VpnExpirationObserved = "vpn.expiration.observed";
    public const string VpnStatusChanged = "vpn.status.changed";
}

public static class AutomationActionKinds
{
    public const string LocalNotification = "local.notification";
}

public static class AutomationOptionProviderKeys
{
    public const string DataSources = "data-sources";
    public const string QuotaWindows = "quota-windows";
    public const string InputModels = "input-models";
}

public sealed class AutomationEvent
{
    public string Type { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string SubjectKey { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string Title { get; init; } = "Oh My PC";
    public string Body { get; init; } = "";
    public JsonObject Fields { get; init; } = new();
}

public sealed class AutomationRuleDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public string EventType { get; set; } = AutomationEventTypes.QuotaObserved;
    public AutomationMatchMode MatchMode { get; set; } = AutomationMatchMode.All;
    public List<AutomationConditionDefinition> Conditions { get; set; } = [];
    public List<AutomationActionDefinition> Actions { get; set; } = [];
    public int CooldownMinutes { get; set; } = 240;
    public bool RespectQuietHours { get; set; } = true;
}

public sealed class AutomationConditionDefinition
{
    public string Field { get; set; } = "";
    public AutomationConditionOperator Operator { get; set; } = AutomationConditionOperator.Equal;
    public AutomationValueKind ValueKind { get; set; } = AutomationValueKind.Text;
    public JsonNode? Value { get; set; }
}

public sealed class AutomationActionDefinition
{
    public string Kind { get; set; } = "";
    public JsonObject Configuration { get; set; } = new();
}

public sealed class LocalNotificationActionOptions
{
    public NotificationChannels Channels { get; set; } = NotificationChannels.Danmaku;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Warning;

    public AutomationActionDefinition ToDefinition() => new()
    {
        Kind = AutomationActionKinds.LocalNotification,
        Configuration = new JsonObject
        {
            ["channels"] = (int)Channels,
            ["severity"] = (int)Severity
        }
    };

    public static LocalNotificationActionOptions FromDefinition(AutomationActionDefinition definition) => new()
    {
        Channels = (NotificationChannels)definition.Configuration["channels"]!.GetValue<int>(),
        Severity = (NotificationSeverity)definition.Configuration["severity"]!.GetValue<int>()
    };
}

public sealed class AutomationRuleState
{
    public string RuleId { get; set; } = "";
    public string SubjectKey { get; set; } = "";
    public DateTimeOffset? LastExecutedAt { get; set; }
}

public sealed class AutomationSourceState
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationEventDescriptor
{
    public string EventType { get; init; } = "";
    public string DisplayNameKey { get; init; } = "";
    public IReadOnlyList<AutomationFieldDescriptor> Fields { get; init; } = [];
}

public sealed class AutomationFieldDescriptor
{
    public string Key { get; init; } = "";
    public string DisplayNameKey { get; init; } = "";
    public AutomationValueKind ValueKind { get; init; }
    public IReadOnlyList<AutomationConditionOperator> Operators { get; init; } = [];
    public string? OptionProviderKey { get; init; }
}

public sealed class AutomationValueOption
{
    public string Value { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public sealed class InputModelStatus
{
    public string Model { get; init; } = "";
    public bool Available { get; init; }
    public long? LatencyMilliseconds { get; init; }
    public string? Error { get; init; }
}

public sealed class VpnAccountDefinition
{
    public string Email { get; set; } = "";
    public string PlanName { get; set; } = "";
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public long TransferLimitBytes { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? ResetDay { get; set; }
    public ProviderStatus Status { get; set; } = ProviderStatus.Unknown;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }

    public long UsedBytes => UploadedBytes + DownloadedBytes;
    public long RemainingBytes => Math.Max(0, TransferLimitBytes - UsedBytes);
    public double RemainingPercent => TransferLimitBytes > 0
        ? Math.Clamp((double)RemainingBytes / TransferLimitBytes * 100d, 0d, 100d)
        : 0;
}

public sealed class VpnSubscriptionSnapshot
{
    public string Email { get; init; } = "";
    public string PlanName { get; init; } = "";
    public long UploadedBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public long TransferLimitBytes { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? ResetDay { get; init; }
}

public sealed class VpnDailyUsagePoint
{
    public DateOnly Date { get; init; }
    public long UploadedBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public long TransferLimitBytes { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    public long UsedBytes => UploadedBytes + DownloadedBytes;
}

public sealed class NotificationMessage
{
    public NotificationOrigin Origin { get; set; } = NotificationOrigin.Application;
    public string Source { get; set; } = "oh-my-pc";
    public string Title { get; set; } = "Oh My PC";
    public string Body { get; set; } = "";
    public NotificationChannels Channels { get; set; }
    public NotificationSeverity Severity { get; set; }
    public string SubjectKey { get; set; } = "";
}

public sealed class NotificationRecord
{
    public string Id { get; init; } = "";
    public NotificationOrigin Origin { get; init; }
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public NotificationChannels Channels { get; init; }
    public NotificationSeverity Severity { get; init; }
    public string SubjectKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class NotificationHistoryQuery
{
    public const int MaximumLimit = 200;

    public string? Source { get; init; }
    public NotificationSeverity? Severity { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public int Limit { get; init; } = 200;
}

public static class NotificationRetentionPolicy
{
    public const int DefaultDays = 30;
    public static readonly IReadOnlyList<int> AllowedDays = [7, 30, 90];

    public static bool IsValid(int days) => days is 7 or 30 or 90;
    public static int Normalize(int days) => IsValid(days) ? days : DefaultDays;
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "zh-CN";
    public bool StartWithWindows { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool DanmakuEnabled { get; set; } = true;
    public bool DanmakuHoldWhenAway { get; set; } = true;
    public string QuietHoursStart { get; set; } = "23:00";
    public string QuietHoursEnd { get; set; } = "08:00";
    public double DanmakuOpacity { get; set; } = 0.92;
    public double DanmakuSpeed { get; set; } = 160;
    public double DanmakuFontSize { get; set; } = 18;
    public int DanmakuDurationSeconds { get; set; } = 10;
    public bool LocalApiEnabled { get; set; }
    public int LocalApiPort { get; set; } = 39417;
    public int NotificationHistoryRetentionDays { get; set; } = NotificationRetentionPolicy.DefaultDays;
    public bool CliProxyAutoStart { get; set; } = true;
    /// <summary>每小时检查一次 GitHub Release 更新；仅安装版生效。</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    /// <summary>键为 ProxyClientKind 名称；记录各客户端上次同步使用的上游范围，缺失时视为全部上游。</summary>
    public Dictionary<string, ProxyClientSyncScope> ClientSyncScopes { get; set; } = [];
}
