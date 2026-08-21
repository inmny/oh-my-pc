namespace OhMyPc.Core.Domain;

public enum DataSourceKind
{
    Sub2Api = 0,
    NewApi = 1,
    ZhipuCodingPlan = 2
}

public enum UsageBreakdownGroup
{
    Tool,
    Model
}

public enum ProviderStatus
{
    Unknown,
    Healthy,
    Unavailable,
    AuthenticationFailed
}

public enum AutomationMatchMode
{
    All,
    Any
}

public enum AutomationValueKind
{
    Text,
    Number,
    Boolean
}

public enum AutomationConditionOperator
{
    LessThanOrEqual,
    GreaterThanOrEqual,
    Equal,
    NotEqual,
    Contains,
    LessThan,
    GreaterThan
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical
}

public enum NotificationOrigin
{
    Application,
    Automation,
    LocalApi
}

[Flags]
public enum NotificationChannels
{
    None = 0,
    Danmaku = 1,
    System = 2,
    Tray = 4
}
