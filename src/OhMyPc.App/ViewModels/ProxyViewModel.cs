using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.CliProxy;

namespace OhMyPc.App.ViewModels;

/// <summary>模型代理标签页：CLIProxyAPI 的安装、进程、上游 Provider/路由配置与客户端同步。</summary>
public sealed class ProxyViewModel : ViewModelBase
{
    private readonly IProxyConfigStore _configStore;
    private readonly ICliProxyInstaller _installer;
    private readonly ICliProxyProcessService _process;
    private readonly IProxyStatusService _status;
    private readonly IClientConfigurator _configurator;
    private readonly IRemoteModelListClient _remoteModels;
    private readonly IModelMetadataProvider _metadata;
    private readonly LocalizationService _text;
    private readonly ILogger<ProxyViewModel> _logger;

    private ProxyConfigSnapshot? _snapshot;
    private bool _isInstalled;
    private bool _installBusy;
    private bool _canMigrate;
    private bool _migrateFromEasyCpa = true;
    private string _installStatusText = "";
    private bool _hasInstallError;
    private bool _isRunning;
    private bool _isStarting;
    private string _stateText = "";
    private string _versionText = "";
    private string _modelCountText = "";
    private string _baseUrlText = "";
    private string _operationText = "";
    private bool _hasOperationError;
    private ProxyProviderItemViewModel? _selectedProvider;
    private string _selectedStrategy = ProxyCatalog.StrategyRoundRobin;
    private bool _sessionAffinity;
    private string _requestRetryText = "3";
    private string _maxRetryIntervalText = "30";
    private string _apiKeysText = "";
    private bool _isProvidersSelected = true;
    private bool _isUnifiedSelected;
    private bool _isRoutingSelected;
    private bool _isClientsSelected;
    private ProxyUnifiedModelRowViewModel? _selectedUnifiedModel;

    public ProxyViewModel(
        IProxyConfigStore configStore,
        ICliProxyInstaller installer,
        ICliProxyProcessService process,
        IProxyStatusService status,
        IClientConfigurator configurator,
        IRemoteModelListClient remoteModels,
        IModelMetadataProvider metadata,
        LocalizationService text,
        ILogger<ProxyViewModel> logger)
    {
        _configStore = configStore;
        _installer = installer;
        _process = process;
        _status = status;
        _configurator = configurator;
        _remoteModels = remoteModels;
        _metadata = metadata;
        _text = text;
        _logger = logger;
        foreach (var kind in Enum.GetValues<ProxyClientKind>())
        {
            Clients.Add(new ProxyClientSyncItemViewModel(kind, text));
        }
        _status.Refreshed += StatusRefreshed;
        _process.StateChanged += ProcessStateChanged;
        InstallCommand = new AsyncCommand(InstallAsync, () => !InstallBusy);
        RedetectCommand = new AsyncCommand(InitializeAsync, () => !InstallBusy);
        StartCommand = new AsyncCommand(() => RunProcessActionAsync(_process.StartAsync), () => IsInstalled && !InstallBusy);
        StopCommand = new AsyncCommand(() => RunProcessActionAsync(_process.StopAsync), () => IsInstalled && !InstallBusy);
        RestartCommand = new AsyncCommand(() => RunProcessActionAsync(_process.RestartAsync), () => IsInstalled && !InstallBusy);
        SaveConfigCommand = new AsyncCommand(SaveConfigAsync, () => IsInstalled);
        AddClaudeCommand = new AsyncCommand(() => { AddProvider(ProxyProviderKind.Claude); return Task.CompletedTask; });
        AddCodexCommand = new AsyncCommand(() => { AddProvider(ProxyProviderKind.Codex); return Task.CompletedTask; });
        SyncClientCommand = new AsyncCommand<ProxyClientKind>(SyncClientAsync);
    }

    public ICommand InstallCommand { get; }
    public ICommand RedetectCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand AddClaudeCommand { get; }
    public ICommand AddCodexCommand { get; }
    public ICommand SyncClientCommand { get; }

    public ObservableCollection<ProxyProviderItemViewModel> Providers { get; } = [];

    public ObservableCollection<ProxyUnifiedModelRowViewModel> UnifiedModels { get; } = [];

    public ProxyUnifiedModelRowViewModel? SelectedUnifiedModel
    {
        get => _selectedUnifiedModel;
        set
        {
            if (Set(ref _selectedUnifiedModel, value)) Raise(nameof(HasSelectedUnifiedModel));
        }
    }

    public bool HasSelectedUnifiedModel => SelectedUnifiedModel is not null;

    public bool HasUnifiedModels => UnifiedModels.Count > 0;

    public ObservableCollection<ProxyClientSyncItemViewModel> Clients { get; } = [];

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (!Set(ref _isInstalled, value)) return;
            Raise(nameof(IsNotInstalled));
            RefreshCommands();
        }
    }

    public bool IsNotInstalled => !IsInstalled;

    public bool InstallBusy
    {
        get => _installBusy;
        private set
        {
            if (!Set(ref _installBusy, value)) return;
            ((AsyncCommand)InstallCommand).Refresh();
            ((AsyncCommand)RedetectCommand).Refresh();
        }
    }

    public bool CanMigrate { get => _canMigrate; private set => Set(ref _canMigrate, value); }

    public bool MigrateFromEasyCpa { get => _migrateFromEasyCpa; set => Set(ref _migrateFromEasyCpa, value); }

    public string InstallStatusText
    {
        get => _installStatusText;
        private set
        {
            if (Set(ref _installStatusText, value)) Raise(nameof(HasInstallStatus));
        }
    }

    public bool HasInstallError { get => _hasInstallError; private set => Set(ref _hasInstallError, value); }
    public bool HasInstallStatus => InstallStatusText.Length > 0;

    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool IsStarting { get => _isStarting; private set => Set(ref _isStarting, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
    public string ModelCountText { get => _modelCountText; private set => Set(ref _modelCountText, value); }
    public string BaseUrlText { get => _baseUrlText; private set => Set(ref _baseUrlText, value); }
    public string OperationText { get => _operationText; private set => Set(ref _operationText, value); }
    public bool HasOperationError { get => _hasOperationError; private set => Set(ref _hasOperationError, value); }

    public ProxyProviderItemViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (Set(ref _selectedProvider, value)) Raise(nameof(HasSelectedProvider));
        }
    }

    public bool HasSelectedProvider => SelectedProvider is not null;

    public string SelectedStrategy { get => _selectedStrategy; set => Set(ref _selectedStrategy, value); }
    public bool SessionAffinity { get => _sessionAffinity; set => Set(ref _sessionAffinity, value); }
    public string RequestRetryText { get => _requestRetryText; set => Set(ref _requestRetryText, value); }
    public string MaxRetryIntervalText { get => _maxRetryIntervalText; set => Set(ref _maxRetryIntervalText, value); }
    public string ApiKeysText { get => _apiKeysText; set => Set(ref _apiKeysText, value); }

    public bool IsProvidersSelected
    {
        get => _isProvidersSelected;
        set => SetSegment(ref _isProvidersSelected, value, nameof(IsProvidersSelected));
    }

    public bool IsUnifiedSelected
    {
        get => _isUnifiedSelected;
        set
        {
            SetSegment(ref _isUnifiedSelected, value, nameof(IsUnifiedSelected));
            if (value) RebuildUnifiedModels();
        }
    }

    public bool IsRoutingSelected
    {
        get => _isRoutingSelected;
        set => SetSegment(ref _isRoutingSelected, value, nameof(IsRoutingSelected));
    }

    public bool IsClientsSelected
    {
        get => _isClientsSelected;
        set => SetSegment(ref _isClientsSelected, value, nameof(IsClientsSelected));
    }

    public async Task InitializeAsync()
    {
        IsInstalled = _installer.IsInstalled();
        CanMigrate = _installer.CanMigrateFromEasyCpa();
        if (IsNotInstalled) MigrateFromEasyCpa = CanMigrate;
        if (IsInstalled)
        {
            try
            {
                await _configStore.EnsureConfigAsync();
                await LoadConfigAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "加载 CLIProxyAPI 配置失败");
                SetOperationError(_text.Format("Proxy_OperationFailed", exception.Message));
            }
        }
        ApplyStatus(await _status.RefreshAsync());
        RebuildClientProviderPicks();
    }

    public async Task InstallAsync()
    {
        InstallBusy = true;
        HasInstallError = false;
        InstallStatusText = _text["Proxy_Installing"];
        try
        {
            var result = await _installer.InstallAsync(new ProxyInstallOptions
            {
                MigrateFromEasyCpa = MigrateFromEasyCpa && CanMigrate
            });
            await InitializeAsync();
            InstallStatusText = result.Migrated ? _text["Proxy_InstallMigrated"] : _text["Proxy_InstallDone"];
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CLIProxyAPI 安装失败");
            InstallStatusText = _text.Format("Proxy_InstallFailed", exception.Message);
            HasInstallError = true;
        }
        finally
        {
            InstallBusy = false;
        }
    }

    public async Task LoadConfigAsync()
    {
        _snapshot = await _configStore.LoadAsync();
        Providers.Clear();
        foreach (var provider in _snapshot.Providers)
        {
            Providers.Add(new ProxyProviderItemViewModel(provider, _text));
        }
        SelectedProvider = Providers.FirstOrDefault();
        var routing = _snapshot.Routing;
        SelectedStrategy = ProxyCatalog.Strategies.Contains(routing.Strategy) ? routing.Strategy : ProxyCatalog.StrategyRoundRobin;
        SessionAffinity = routing.SessionAffinity;
        RequestRetryText = routing.RequestRetry.ToString();
        MaxRetryIntervalText = routing.MaxRetryInterval.ToString();
        ApiKeysText = string.Join(Environment.NewLine, _snapshot.Access.ApiKeys);
        BaseUrlText = _snapshot.Access.GetBaseUrl();
        RebuildDerived();
    }

    public async Task SaveConfigAsync()
    {
        if (!IsInstalled) return;
        try
        {
            var snapshot = BuildSnapshot();
            await _configStore.SaveAsync(snapshot);
            _snapshot = snapshot;
            HasOperationError = false;
            OperationText = _text["Proxy_Saved"];
            ApplyStatus(await _status.RefreshAsync());
            RebuildDerived();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存 CLIProxyAPI 配置失败");
            SetOperationError(_text.Format("Proxy_OperationFailed", exception.Message));
        }
    }

    public void AddProvider(ProxyProviderKind kind)
    {
        var item = new ProxyProviderItemViewModel(new ProxyProviderConfig { Kind = kind }, _text);
        Providers.Add(item);
        SelectedProvider = item;
    }

    public void RemoveSelectedProvider()
    {
        if (SelectedProvider is null) return;
        var index = Providers.IndexOf(SelectedProvider);
        Providers.Remove(SelectedProvider);
        SelectedProvider = Providers.Count == 0 ? null : Providers[Math.Min(index, Providers.Count - 1)];
    }

    public void AddModelToSelected(ProxyModelConfig model)
    {
        if (SelectedProvider is null) return;
        SelectedProvider.Models.Add(new ProxyModelItemViewModel(model));
        SelectedProvider.RaiseModelCount();
        RebuildDerived();
    }

    public void UpdateSelectedModel(ProxyModelConfig model)
    {
        var provider = SelectedProvider;
        if (provider?.SelectedModel is null) return;
        var index = provider.Models.IndexOf(provider.SelectedModel);
        if (index < 0) return;
        var item = new ProxyModelItemViewModel(model);
        provider.Models[index] = item;
        provider.SelectedModel = item;
        RebuildDerived();
    }

    public void RemoveSelectedModel()
    {
        var provider = SelectedProvider;
        if (provider?.SelectedModel is null) return;
        provider.Models.Remove(provider.SelectedModel);
        provider.SelectedModel = null;
        provider.RaiseModelCount();
        RebuildDerived();
    }

    /// <summary>拉取远端模型 id 列表并匹配 models.dev 元数据，生成导入对话框的行；「模型统一」中的同名配置优先生效。</summary>
    public async Task<IReadOnlyList<ProxyImportModelRow>> PrepareImportRowsAsync(ProxyProviderConfig provider)
    {
        var ids = await _remoteModels.FetchModelIdsAsync(provider);
        var metadata = await _metadata.GetAsync();
        var unified = BuildUnifiedLookup();
        var existing = provider.Models.Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProxyImportModelRow>();
        foreach (var id in ids)
        {
            rows.Add(BuildImportRow(id, existing.Contains(id), ModelMetadataParser.Find(metadata, id), unified));
        }
        foreach (var name in provider.Models.Select(model => model.Name)
                     .Where(name => !ids.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            rows.Add(BuildImportRow(name, exists: true, ModelMetadataParser.Find(metadata, name), unified));
        }
        return rows;
    }

    private static ProxyImportModelRow BuildImportRow(
        string id, bool exists, ModelMetadata? meta, IReadOnlyDictionary<string, ProxyModelConfig> unified)
    {
        unified.TryGetValue(id, out var preset);
        return new ProxyImportModelRow
        {
            Id = id,
            Exists = exists,
            Checked = !exists,
            ContextWindow = preset?.MaxContextLength ?? meta?.ContextWindow,
            Levels = preset is { ThinkingLevels.Count: > 0 } ? [.. preset.ThinkingLevels] : meta?.ThinkingLevels ?? [],
            InputModalities = preset is { InputModalities.Count: > 0 } ? [.. preset.InputModalities] : meta?.InputModalities ?? [],
            OutputModalities = preset is { OutputModalities.Count: > 0 } ? [.. preset.OutputModalities] : meta?.OutputModalities ?? [],
            Cost = meta?.Cost is { IsEmpty: false } cost ? cost : null
        };
    }

    /// <summary>应用导入选择：勾选的新模型追加，勾选的既有模型用元数据更新（保留别名与既有费用）。</summary>
    public void ApplyImportedModels(IReadOnlyList<ProxyImportModelRow> rows)
    {
        var provider = SelectedProvider;
        if (provider is null || rows.Count == 0) return;
        foreach (var row in rows.Where(row => row.Checked))
        {
            var imported = new ProxyModelConfig
            {
                Name = row.Id,
                MaxContextLength = row.ContextWindow,
                ThinkingLevels = [.. row.Levels],
                InputModalities = [.. row.InputModalities],
                OutputModalities = [.. row.OutputModalities],
                Cost = row.Cost
            };
            var index = provider.Models.ToList().FindIndex(model =>
                string.Equals(model.Source.Name, row.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                provider.Models.Add(new ProxyModelItemViewModel(imported));
                continue;
            }
            var existing = provider.Models[index].Source;
            imported.Alias = existing.Alias;
            imported.Cost ??= existing.Cost;
            provider.Models[index] = new ProxyModelItemViewModel(imported);
        }
        provider.SelectedModel = null;
        provider.RaiseModelCount();
        RebuildDerived();
    }

    /// <summary>把统一编辑的上下文/档位/模态应用到所有 Provider 的同名模型，保留各自别名与费用。</summary>
    public void ApplyUnifiedModel(ProxyUnifiedModelRowViewModel row, UnifiedModelEdit edit)
    {
        foreach (var provider in Providers)
        {
            for (var i = 0; i < provider.Models.Count; i++)
            {
                var source = provider.Models[i].Source;
                if (!string.Equals(source.Name, row.Name, StringComparison.OrdinalIgnoreCase)) continue;
                provider.Models[i] = new ProxyModelItemViewModel(new ProxyModelConfig
                {
                    Name = source.Name,
                    Alias = source.Alias,
                    MaxContextLength = edit.ContextLength,
                    ThinkingLevels = [.. edit.ThinkingLevels],
                    InputModalities = [.. edit.InputModalities],
                    OutputModalities = [.. edit.OutputModalities],
                    Cost = source.Cost
                });
            }
        }
        RebuildDerived();
    }

    /// <summary>所有 provider 的模型按上游名聚合（模型统一分段与导入优先级共用）。</summary>
    private static List<IGrouping<string, ProxyModelItemViewModel>> ModelGroupsByName(
        IReadOnlyList<ProxyProviderItemViewModel> providers) =>
        [.. providers
            .SelectMany(provider => provider.Models)
            .GroupBy(model => model.Source.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)];

    /// <summary>模型统一配置查找表：同名模型取第一个实例的当前配置。</summary>
    private Dictionary<string, ProxyModelConfig> BuildUnifiedLookup() =>
        ModelGroupsByName(Providers).ToDictionary(
            group => group.Key,
            group => group.First().Source,
            StringComparer.OrdinalIgnoreCase);

    private void RebuildUnifiedModels()
    {
        UnifiedModels.Clear();
        foreach (var group in ModelGroupsByName(Providers))
        {
            var models = group.Select(item => item.Source).ToList();
            var consistent = models.Select(model => model.MaxContextLength).Distinct().Count() <= 1
                && models.Select(model => string.Join(",", ProxyMappers.OrderLevels(model.ThinkingLevels))).Distinct().Count() <= 1
                && models.Select(model => $"{string.Join(",", model.InputModalities)}|{string.Join(",", model.OutputModalities)}").Distinct().Count() <= 1;
            UnifiedModels.Add(new ProxyUnifiedModelRowViewModel
            {
                Name = group.Key,
                ProviderCount = models.Count,
                IsConsistent = consistent,
                ContextWindow = models[0].MaxContextLength,
                ThinkingLevels = ProxyMappers.OrderLevels(models[0].ThinkingLevels),
                InputModalities = [.. models[0].InputModalities],
                OutputModalities = [.. models[0].OutputModalities]
            });
        }
        SelectedUnifiedModel = null;
        Raise(nameof(HasUnifiedModels));
    }

    public void RefreshLocalization()
    {
        foreach (var provider in Providers) provider.RefreshLocalization();
        foreach (var client in Clients) client.RefreshLocalization();
        ApplyStatus(_status.Last);
        Raise(nameof(SelectedStrategy));
        // 范围勾选列表的展示文案来自 provider，语言切换后重建（勾选状态按 Key 保留）
        RebuildClientProviderPicks();
    }

    private ProxyConfigSnapshot BuildSnapshot() => new()
    {
        Providers = [.. Providers.Select(provider => provider.ToConfig())],
        Routing = new ProxyRoutingConfig
        {
            Strategy = ProxyCatalog.Strategies.Contains(SelectedStrategy) ? SelectedStrategy : ProxyCatalog.StrategyRoundRobin,
            SessionAffinity = SessionAffinity,
            RequestRetry = ParseInt(RequestRetryText, 3),
            MaxRetryInterval = ParseInt(MaxRetryIntervalText, 30)
        },
        Access = new ProxyAccessConfig
        {
            Host = _snapshot?.Access.Host ?? "127.0.0.1",
            Port = _snapshot?.Access.Port ?? 8317,
            ApiKeys = [.. ApiKeysText.Split('\n').Select(key => key.Trim()).Where(key => key.Length > 0)]
        }
    };

    private async Task RunProcessActionAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            OperationText = "";
            await action(CancellationToken.None);
            ApplyStatus(await _status.RefreshAsync());
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CLIProxyAPI 进程操作失败");
            ApplyStatus(await _status.RefreshAsync());
            SetOperationError(_text.Format("Proxy_OperationFailed", exception.Message));
        }
    }

    private async Task SyncClientAsync(ProxyClientKind kind)
    {
        var item = Clients.First(client => client.Client == kind);
        var selectedProviders = Providers.Where(provider => item.IsProviderSelected(provider.Key)).ToList();
        if (selectedProviders.Count == 0)
        {
            item.LastSyncText = _text["Proxy_SyncEmptyScope"];
            return;
        }
        var baseUrl = _snapshot?.Access.GetBaseUrl() ?? "";
        ClientSyncPlan plan;
        if (item.IsAllProviders)
        {
            var apiKey = _snapshot?.Access.ApiKeys.FirstOrDefault() ?? "";
            if (apiKey.Length == 0)
            {
                item.LastSyncText = _text["Proxy_SyncNoKey"];
                return;
            }
            plan = new ClientSyncPlan
            {
                Client = kind,
                ProviderId = string.IsNullOrWhiteSpace(item.ProviderId)
                    ? CliProxyClientConfigurator.DefaultProviderId
                    : item.ProviderId.Trim(),
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Models = [.. selectedProviders
                    .SelectMany(provider => provider.Models.Select(model => new ClientSyncModel(model.Source, provider.Kind)))]
            };
        }
        else
        {
            // 指定上游：客户端直连各上游的真实地址与密钥，不经网关
            plan = new ClientSyncPlan
            {
                Client = kind,
                BaseUrl = baseUrl,
                Upstreams = [.. selectedProviders.Select(provider => new ClientSyncUpstream(
                    provider.Key,
                    provider.TitleText,
                    provider.BaseUrl.Trim(),
                    provider.ApiKey.Trim(),
                    provider.Kind,
                    [.. provider.Models.Select(model => model.Source)]))]
            };
        }
        try
        {
            var result = await _configurator.SyncAsync(plan);
            item.ProviderId = result.ProviderId;
            item.LastSyncText = _text.Format("Proxy_SyncDone", result.ModelCount);
            if (PersistScopeAsync is not null) await PersistScopeAsync(kind, item.CaptureScope());
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "同步 {Client} 配置失败", kind);
            item.LastSyncText = _text.Format("Proxy_SyncFailed", exception.Message);
        }
    }

    /// <summary>由 MainViewModel 注入：把本次同步使用的范围写入应用设置。</summary>
    public Func<ProxyClientKind, ProxyClientSyncScope, Task>? PersistScopeAsync { get; set; }

    /// <summary>把持久化的范围应用到各客户端卡片（Provider 列表加载后调用，键失配的条目保持默认）。</summary>
    public void ApplyClientScopes(IReadOnlyDictionary<string, ProxyClientSyncScope> scopes)
    {
        foreach (var client in Clients)
        {
            if (scopes.TryGetValue(client.Client.ToString(), out var scope)) client.ApplyScope(scope);
        }
    }

    private void RebuildClientProviderPicks()
    {
        var picks = Providers.Select(provider => (provider.Key, provider.SyncDisplay)).ToList();
        foreach (var client in Clients)
        {
            client.UpdateProviders(picks);
        }
    }

    private void RebuildDerived()
    {
        RebuildClientProviderPicks();
        RebuildUnifiedModels();
    }

    private void ApplyStatus(ProxyServiceStatus status)
    {
        IsRunning = status.State == ProxyProcessState.Running;
        IsStarting = status.State == ProxyProcessState.Starting || _process.State == ProxyProcessState.Starting;
        StateText = status.State switch
        {
            ProxyProcessState.Running => _text["Proxy_StateRunning"],
            ProxyProcessState.Starting => _text["Proxy_StateStarting"],
            _ => _text["Proxy_StateStopped"]
        };
        VersionText = status.Version is null ? "" : _text.Format("Proxy_Version", status.Version);
        ModelCountText = IsRunning ? _text.Format("Proxy_ModelCount", status.ModelCount) : "";
        if (!string.IsNullOrEmpty(status.BaseUrl)) BaseUrlText = status.BaseUrl;
    }

    private void SetOperationError(string message)
    {
        HasOperationError = true;
        OperationText = message;
    }

    private void RefreshCommands()
    {
        ((AsyncCommand)StartCommand).Refresh();
        ((AsyncCommand)StopCommand).Refresh();
        ((AsyncCommand)RestartCommand).Refresh();
        ((AsyncCommand)SaveConfigCommand).Refresh();
    }

    private void SetSegment(ref bool field, bool value, string propertyName)
    {
        if (!Set(ref field, value, propertyName) || !value) return;
        foreach (var other in new[]
                 {
                     nameof(IsProvidersSelected), nameof(IsUnifiedSelected),
                     nameof(IsRoutingSelected), nameof(IsClientsSelected)
                 })
        {
            if (other != propertyName) Raise(other);
        }
    }

    private void StatusRefreshed(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyStatus(_status.Last));

    private void ProcessStateChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsStarting = _process.State == ProxyProcessState.Starting;
            ApplyStatus(_status.Last);
        });

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? value : fallback;
}

/// <summary>上游 Provider 的编辑行。</summary>
public sealed class ProxyProviderItemViewModel(ProxyProviderConfig config, LocalizationService text) : ViewModelBase
{
    private string _remark = config.Remark ?? "";
    private string _apiKey = config.ApiKey;
    private string _baseUrl = config.BaseUrl;
    private string _priorityText = config.Priority?.ToString() ?? "";
    private ProxyModelItemViewModel? _selectedModel;

    public ProxyProviderKind Kind { get; } = config.Kind;

    public ObservableCollection<ProxyModelItemViewModel> Models { get; } =
        [.. config.Models.Select(model => new ProxyModelItemViewModel(model))];

    public ProxyModelItemViewModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Set(ref _selectedModel, value)) Raise(nameof(HasSelectedModel));
        }
    }

    public bool HasSelectedModel => SelectedModel is not null;

    public string KindText => Kind == ProxyProviderKind.Claude ? text["Proxy_KindClaude"] : text["Proxy_KindCodex"];

    /// <summary>卡片标题：优先显示用户填写的名称，否则显示类型。</summary>
    public string TitleText => string.IsNullOrWhiteSpace(Remark) ? KindText : Remark;

    public string ModelCountText => text.Format("Proxy_ModelCount", Models.Count);
    public string Remark { get => _remark; set { if (Set(ref _remark, value)) Raise(nameof(TitleText)); } }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string PriorityText { get => _priorityText; set => Set(ref _priorityText, value); }

    public ProxyProviderConfig ToConfig() => new()
    {
        Kind = Kind,
        ApiKey = ApiKey.Trim(),
        BaseUrl = BaseUrl.Trim(),
        Remark = string.IsNullOrWhiteSpace(Remark) ? null : Remark.Trim(),
        Priority = int.TryParse(PriorityText.Trim(), out var priority) ? priority : null,
        Models = [.. Models.Select(model => model.Source)]
    };

    public void RaiseModelCount() => Raise(nameof(ModelCountText));

    /// <summary>同步范围的稳定标识，与 CliProxyConfigStore 复用条目的判定键（api-key + base-url）一致。</summary>
    public string Key => $"{ApiKey}|{BaseUrl}";

    public string SyncDisplay => $"{TitleText} · {ModelCountText}";

    public void RefreshLocalization()
    {
        Raise(nameof(KindText));
        Raise(nameof(TitleText));
        Raise(nameof(ModelCountText));
        Raise(nameof(SyncDisplay));
    }
}

/// <summary>模型的只读展示行；编辑通过 ProxyModelDialog 完成。</summary>
public sealed class ProxyModelItemViewModel
{
    public ProxyModelItemViewModel(ProxyModelConfig source) => Source = source;

    public ProxyModelConfig Source { get; }

    public string Name => Source.Name;

    public string Alias => Source.Alias ?? "";

    public string LevelsText => ProxyMappers.OrderLevels(Source.ThinkingLevels).Count == 0
        ? "-"
        : string.Join(", ", ProxyMappers.OrderLevels(Source.ThinkingLevels));

    public string ContextText => Source.MaxContextLength?.ToString("N0") ?? "-";

    public string ModalitiesText =>
        $"{string.Join("+", Source.InputModalities)} → {string.Join("+", Source.OutputModalities)}";
}

/// <summary>客户端同步卡片：同步范围选择与最近一次同步结果；默认模型由用户在客户端内自行选择。</summary>
public sealed class ProxyClientSyncItemViewModel(ProxyClientKind client, LocalizationService text) : ViewModelBase
{
    private readonly ObservableCollection<ProxyProviderPickItemViewModel> _providerPicks = [];
    private bool _isAllProviders = true;
    private string _providerId = CliProxyClientConfigurator.DefaultProviderId;
    private string _lastSyncText = "";

    public ProxyClientKind Client { get; } = client;

    public string NameText => Client switch
    {
        ProxyClientKind.Zcode => "zcode",
        ProxyClientKind.Opencode => "opencode",
        _ => "dsh"
    };

    public bool ConfigFileExists => Client switch
    {
        ProxyClientKind.Zcode => File.Exists(ProxyClientPaths.ZcodeDesktopConfig) || File.Exists(ProxyClientPaths.ZcodeCliConfig),
        ProxyClientKind.Opencode => File.Exists(ProxyClientPaths.OpencodeConfig),
        _ => File.Exists(ProxyClientPaths.DshSettings)
    };

    public string ConfigFileText => ConfigFileExists ? text["Proxy_ClientConfigFound"] : "";

    public bool IsAllProviders
    {
        get => _isAllProviders;
        set
        {
            if (Set(ref _isAllProviders, value))
            {
                Raise(nameof(ShowProviderPicks));
                Raise(nameof(ScopeIndex));
            }
        }
    }

    public bool ShowProviderPicks => !IsAllProviders;

    /// <summary>范围下拉的展示项；索引 0=全部上游，1=指定上游。</summary>
    public IReadOnlyList<string> ScopeOptions => [text["Proxy_ScopeAll"], text["Proxy_ScopeSelected"]];

    public int ScopeIndex
    {
        get => IsAllProviders ? 0 : 1;
        set
        {
            var all = value == 0;
            if (all == IsAllProviders) return;
            IsAllProviders = all;
        }
    }

    public ObservableCollection<ProxyProviderPickItemViewModel> ProviderPicks => _providerPicks;

    public string ProviderId { get => _providerId; set => Set(ref _providerId, value); }
    public string LastSyncText { get => _lastSyncText; set => Set(ref _lastSyncText, value); }

    /// <summary>全部模式选中所有上游；指定模式只选中勾选列表里的上游。</summary>
    public bool IsProviderSelected(string providerKey) =>
        IsAllProviders || _providerPicks.FirstOrDefault(pick => pick.Key == providerKey)?.IsChecked == true;

    public void UpdateProviders(IReadOnlyList<(string Key, string Display)> picks)
    {
        var previous = _providerPicks.ToDictionary(pick => pick.Key, pick => pick.IsChecked);
        _providerPicks.Clear();
        foreach (var (key, display) in picks)
        {
            _providerPicks.Add(new ProxyProviderPickItemViewModel(key, display)
            {
                IsChecked = previous.GetValueOrDefault(key, true)
            });
        }
        Raise(nameof(ProviderPicks));
        Raise(nameof(ConfigFileText));
    }

    public void ApplyScope(ProxyClientSyncScope scope)
    {
        IsAllProviders = scope.AllProviders;
        if (!scope.AllProviders)
        {
            foreach (var pick in _providerPicks)
            {
                pick.IsChecked = scope.ProviderKeys.Contains(pick.Key);
            }
        }
    }

    public ProxyClientSyncScope CaptureScope() => new()
    {
        AllProviders = IsAllProviders,
        ProviderKeys = [.. _providerPicks.Where(pick => pick.IsChecked).Select(pick => pick.Key)]
    };

    public void RefreshLocalization()
    {
        Raise(nameof(ConfigFileText));
        Raise(nameof(ScopeOptions));
        Raise(nameof(ScopeIndex));
    }
}

/// <summary>同步范围勾选列表中的一行上游 Provider。</summary>
public sealed class ProxyProviderPickItemViewModel(string key, string display) : ViewModelBase
{
    private bool _isChecked = true;

    public string Key { get; } = key;
    public string Display { get; } = display;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>导入对话框中的一行：远端模型 id + 匹配到的 models.dev 元数据。</summary>
public sealed class ProxyImportModelRow : ViewModelBase
{
    private bool _checked;

    public required string Id { get; init; }
    public required bool Exists { get; init; }
    public bool Checked { get => _checked; set => Set(ref _checked, value); }
    public long? ContextWindow { get; init; }
    public IReadOnlyList<string> Levels { get; init; } = [];
    public IReadOnlyList<string> InputModalities { get; init; } = [];
    public IReadOnlyList<string> OutputModalities { get; init; } = [];
    public ProxyModelCost? Cost { get; init; }

    public bool HasMetadata => ContextWindow is not null || Levels.Count > 0 || Cost is not null;
    public string ContextText => ContextWindow?.ToString("N0") ?? "-";
    public string LevelsText => Levels.Count == 0 ? "-" : string.Join(", ", Levels);
    public string ModalitiesText => $"{string.Join("+", InputModalities)} → {string.Join("+", OutputModalities)}";
    public string CostText => Cost is null ? "-" : $"{Cost.Input ?? 0:0.####} / {Cost.Output ?? 0:0.####}";
}

/// <summary>统一配置分段的行：跨 Provider 聚合后的同名模型。</summary>
public sealed class ProxyUnifiedModelRowViewModel
{
    public required string Name { get; init; }
    public required int ProviderCount { get; init; }
    public required bool IsConsistent { get; init; }
    public long? ContextWindow { get; init; }
    public IReadOnlyList<string> ThinkingLevels { get; init; } = [];
    public IReadOnlyList<string> InputModalities { get; init; } = [];
    public IReadOnlyList<string> OutputModalities { get; init; } = [];

    public bool IsInconsistent => !IsConsistent;

    /// <summary>不一致时展示为空，由一致性列提示。</summary>
    public string ContextText => IsConsistent ? ContextWindow?.ToString("N0") ?? "-" : "";
    public string LevelsText => IsConsistent ? ThinkingLevels.Count == 0 ? "-" : string.Join(", ", ThinkingLevels) : "";
}

/// <summary>统一编辑对话框的输出。</summary>
public sealed record UnifiedModelEdit(
    long? ContextLength,
    IReadOnlyList<string> ThinkingLevels,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities);
