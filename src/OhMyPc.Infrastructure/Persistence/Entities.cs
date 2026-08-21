namespace OhMyPc.Infrastructure.Persistence;

public sealed class DataSourceEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Kind { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ModelStatusUrl { get; set; } = "";
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; }
    public int Status { get; set; }
    public string? LastAttemptAt { get; set; }
    public string? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public CredentialEntity? Credential { get; set; }
    public ICollection<QuotaCurrentEntity> Quotas { get; set; } = [];
}

public sealed class CredentialEntity
{
    public string SourceId { get; set; } = "";
    public byte[] EncryptedValue { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
    public DataSourceEntity Source { get; set; } = null!;
}

public sealed class DailyUsageEntity
{
    public string Date { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Client { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public long TotalTokens { get; set; }
    public long MessageCount { get; set; }
    public long ActiveTimeMs { get; set; }
    public long CostMicroUsd { get; set; }
    public string ObservedAt { get; set; } = "";
}

public sealed class QuotaCurrentEntity
{
    public string SourceId { get; set; } = "";
    public string WindowKey { get; set; } = "";
    public string Label { get; set; } = "";
    public double Used { get; set; }
    public double? Limit { get; set; }
    public double? ProgressLimit { get; set; }
    public double? Remaining { get; set; }
    public string Unit { get; set; } = "";
    public string? ResetAt { get; set; }
    public string ObservedAt { get; set; } = "";
    public int Status { get; set; }
    public string? Detail { get; set; }
    public DataSourceEntity Source { get; set; } = null!;
}

public sealed class AutomationRuleEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string EventType { get; set; } = "";
    public int MatchMode { get; set; }
    public string ConditionsJson { get; set; } = "[]";
    public string ActionsJson { get; set; } = "[]";
    public int CooldownMinutes { get; set; }
    public bool RespectQuietHours { get; set; }
    public ICollection<AutomationRuleStateEntity> States { get; set; } = [];
}

public sealed class AutomationRuleStateEntity
{
    public string RuleId { get; set; } = "";
    public string SubjectKey { get; set; } = "";
    public string? LastExecutedAt { get; set; }
    public AutomationRuleEntity Rule { get; set; } = null!;
}

public sealed class AutomationSourceStateEntity
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class VpnAccountEntity
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public byte[] EncryptedAuthData { get; set; } = [];
    public string PlanName { get; set; } = "";
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public long TransferLimitBytes { get; set; }
    public string? ExpiresAt { get; set; }
    public int? ResetDay { get; set; }
    public int Status { get; set; }
    public string? LastAttemptAt { get; set; }
    public string? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class VpnDailyUsageEntity
{
    public string Date { get; set; } = "";
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public long TransferLimitBytes { get; set; }
    public string ObservedAt { get; set; } = "";
}

public sealed class NotificationEntity
{
    public string Id { get; set; } = "";
    public int Origin { get; set; }
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Channels { get; set; }
    public int Severity { get; set; }
    public string SubjectKey { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string JsonValue { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
