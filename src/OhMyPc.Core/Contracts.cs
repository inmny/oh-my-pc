using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

public interface IAppStore
{
    Task<IReadOnlyList<DataSourceDefinition>> ListDataSourcesAsync(CancellationToken cancellationToken = default);
    Task<DataSourceDefinition?> GetDataSourceAsync(string id, CancellationToken cancellationToken = default);
    Task SaveDataSourceAsync(DataSourceDefinition source, string? apiKey, CancellationToken cancellationToken = default);
    Task DeleteDataSourceAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateDataSourceHealthAsync(DataSourceDefinition source, CancellationToken cancellationToken = default);
    Task<string?> GetCredentialAsync(string sourceId, CancellationToken cancellationToken = default);

    Task UpsertUsageAsync(IReadOnlyCollection<UsageObservation> observations, CancellationToken cancellationToken = default);
    Task ReplaceUsageAsync(
        IReadOnlyCollection<UsageObservation> observations,
        IReadOnlyCollection<UsageObservationScope> scopes,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageTrendPoint>> QueryUsageAsync(DateOnly from, DateOnly to, string? client = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageBreakdownPoint>> QueryUsageBreakdownAsync(DateOnly from, DateOnly to, UsageBreakdownGroup group, CancellationToken cancellationToken = default);
    Task<UsageTrendPoint> GetTodayUsageAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuotaSnapshot>> ListCurrentQuotasAsync(CancellationToken cancellationToken = default);
    Task ReplaceCurrentQuotasAsync(string sourceId, IReadOnlyCollection<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default);

    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task SaveNotificationAsync(NotificationRecord notification, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationRecord>> QueryNotificationsAsync(NotificationHistoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListNotificationSourcesAsync(CancellationToken cancellationToken = default);
    Task DeleteNotificationAsync(string id, CancellationToken cancellationToken = default);
    Task<int> DeleteNotificationsThroughAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> PruneNotificationsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task<VpnAccountDefinition?> GetVpnAccountAsync(CancellationToken cancellationToken = default);
    Task<string?> GetVpnAuthDataAsync(CancellationToken cancellationToken = default);
    Task SaveVpnAccountAsync(VpnAccountDefinition account, string? authData = null, CancellationToken cancellationToken = default);
    Task UpsertVpnDailyUsageAsync(VpnDailyUsagePoint point, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VpnDailyUsagePoint>> QueryVpnDailyUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
    Task DeleteVpnAccountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationRuleDefinition>> ListRulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationRuleDefinition>> ListRulesForEventAsync(string eventType, CancellationToken cancellationToken = default);
    Task SaveRuleAsync(AutomationRuleDefinition rule, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(string id, CancellationToken cancellationToken = default);
    Task<AutomationRuleState?> GetRuleStateAsync(string ruleId, string subjectKey, CancellationToken cancellationToken = default);
    Task SaveRuleStateAsync(AutomationRuleState state, CancellationToken cancellationToken = default);
    Task<AutomationSourceState?> GetSourceStateAsync(string key, CancellationToken cancellationToken = default);
    Task SaveSourceStateAsync(AutomationSourceState state, CancellationToken cancellationToken = default);
}

public interface ILocalUsageCollector
{
    Task<IReadOnlyList<UsageObservation>> CollectAsync(bool fullHistory, CancellationToken cancellationToken = default);
}

public interface IQuotaProvider
{
    DataSourceKind Kind { get; }
    Task<QuotaPollResult> PollAsync(DataSourceDefinition source, string apiKey, CancellationToken cancellationToken = default);
}

public sealed class QuotaPollResult
{
    public IReadOnlyList<QuotaSnapshot> Snapshots { get; init; } = [];
    public ProviderStatus Status { get; init; } = ProviderStatus.Healthy;
    public string? Error { get; init; }
}

public interface INotificationSink
{
    Task<NotificationRecord> PublishAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}

public interface INotificationFeed
{
    event EventHandler<NotificationRecord>? Published;
}

/// <summary>用户是否在电脑前：空闲超时或会话锁定视为离开，出现输入或解锁即恢复。</summary>
public interface IUserPresenceService
{
    bool IsAway { get; }
    event EventHandler? StateChanged;
}

public interface ITextLocalizer
{
    string this[string key] { get; }
    string Format(string key, params object?[] arguments);
    string GetEnum(Enum value);
}

public interface IAutomationEventPublisher
{
    Task PublishAsync(AutomationEvent automationEvent, CancellationToken cancellationToken = default);
}

public interface IAutomationActionHandler
{
    string Kind { get; }
    Task ExecuteAsync(AutomationActionDefinition action, AutomationEvent automationEvent, CancellationToken cancellationToken = default);
}

public interface IAutomationEventDescriptorProvider
{
    IReadOnlyList<AutomationEventDescriptor> Descriptors { get; }
}

public interface IAutomationValueOptionsProvider
{
    string Key { get; }
    Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(CancellationToken cancellationToken = default);
}

public interface IAutomationCatalog
{
    IReadOnlyList<AutomationEventDescriptor> Events { get; }
    Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(string providerKey, CancellationToken cancellationToken = default);
}

public interface IInputStatusClient
{
    Task<IReadOnlyList<InputModelStatus>> GetModelsAsync(Uri statusEndpoint, CancellationToken cancellationToken = default);
}

public interface IVpnQuotaClient
{
    Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<VpnSubscriptionSnapshot> GetSubscriptionAsync(string authData, CancellationToken cancellationToken = default);
}

public interface IProxyConfigStore
{
    Task<ProxyConfigSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProxyConfigSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<bool> EnsureConfigAsync(CancellationToken cancellationToken = default);
}

public interface IProxyStatusService
{
    event EventHandler? Refreshed;
    ProxyServiceStatus Last { get; }
    Task<ProxyServiceStatus> RefreshAsync(CancellationToken cancellationToken = default);
}

public interface ICliProxyInstaller
{
    bool IsInstalled();
    string? GetInstalledVersion();
    bool CanMigrateFromEasyCpa();
    Task<ProxyInstallResult> InstallAsync(ProxyInstallOptions options, CancellationToken cancellationToken = default);
}

public interface ICliProxyProcessService
{
    event EventHandler? StateChanged;
    ProxyProcessState State { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
}

public interface IClientConfigurator
{
    Task<ClientSyncResult> SyncAsync(ClientSyncPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>从上游 provider（中转站）拉取其可用的模型 id 列表。</summary>
public interface IRemoteModelListClient
{
    Task<IReadOnlyList<string>> FetchModelIdsAsync(ProxyProviderConfig provider, CancellationToken cancellationToken = default);
}

/// <summary>提供 models.dev 聚合的模型元数据（上下文/模态/思考档位/费用），按模型 id 检索。</summary>
public interface IModelMetadataProvider
{
    Task<IReadOnlyDictionary<string, ModelMetadata>> GetAsync(CancellationToken cancellationToken = default);
}
