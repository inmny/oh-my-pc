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

    Task<IReadOnlyList<DshServerDefinition>> ListDshServersAsync(CancellationToken cancellationToken = default);
    /// <summary>password 为 null 时保留已存密码，非 null 时覆盖。</summary>
    Task SaveDshServerAsync(DshServerDefinition server, string? password, CancellationToken cancellationToken = default);
    Task DeleteDshServerAsync(string id, CancellationToken cancellationToken = default);
    Task<string?> GetDshPasswordAsync(string serverId, CancellationToken cancellationToken = default);
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

/// <summary>本机 dsh web 实例的生命周期管理。</summary>
public interface ILocalDshManager
{
    DshInstanceSnapshot Snapshot { get; }
    event EventHandler? StateChanged;
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task StartAsync(int port, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>单台远端服务器的 DSH 编排；实例由工厂按服务器定义创建，不持有跨调用状态。</summary>
public interface IRemoteDshService
{
    Task<DshProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken = default);
    Task UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken = default);
    /// <summary>后台拉起 dsh web，成功返回带 token 的面板地址（等待日志输出，超时返回 null）。</summary>
    Task<string?> StartAsync(DshLaunchOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    /// <summary>从远端日志读取最近一次 dsh web 打印的面板地址；未运行或未记录时返回 null。</summary>
    Task<string?> GetPanelUrlAsync(CancellationToken cancellationToken = default);
}

/// <summary>SSH 本地端口转发：把远端回环的 dsh web 暴露到本机回环端口。</summary>
public interface IDshTunnelService
{
    event EventHandler? StateChanged;
    IReadOnlyList<DshTunnelHandle> Active { get; }
    Task<DshTunnelHandle> OpenAsync(DshServerDefinition server, CancellationToken cancellationToken = default);
    void Close(string serverId);
    bool IsOpen(string serverId);
}
