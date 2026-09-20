using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.App.ViewModels;

/// <summary>DSH 页面：本机实例管理 + SSH 远端服务器（安装/更新/启停/隧道转发）。</summary>
public sealed partial class DshViewModel : ObservableObject
{
    private const int LocalForwardPortBase = 13080;

    private readonly IAppStore _store;
    private readonly ILocalDshManager _local;
    private readonly IDshTunnelService _tunnels;
    private readonly DshRemoteServiceFactory _remoteFactory;
    private readonly DshVersionClient _versions;
    private readonly DshConfigSyncService _configSync;
    private readonly LocalizationService _text;
    private readonly ILogger<DshViewModel> _logger;

    public ObservableCollection<DshServerItemViewModel> Servers { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private bool _pageBusy;
    [ObservableProperty] private string _localStateText = "";
    [ObservableProperty] private string _localVersionText = "";
    [ObservableProperty] private string _localPortText = "3080";
    [ObservableProperty] private string? _localPanelUrl;
    [ObservableProperty] private bool _localHasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    private string? _latestVersion;

    public DshViewModel(
        IAppStore store,
        ILocalDshManager local,
        IDshTunnelService tunnels,
        DshRemoteServiceFactory remoteFactory,
        DshVersionClient versions,
        DshConfigSyncService configSync,
        LocalizationService text,
        ILogger<DshViewModel> logger)
    {
        _store = store;
        _local = local;
        _tunnels = tunnels;
        _remoteFactory = remoteFactory;
        _versions = versions;
        _configSync = configSync;
        _text = text;
        _logger = logger;
        _local.StateChanged += (_, _) => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(ApplyLocalSnapshot);
        _tunnels.StateChanged += (_, _) => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(RefreshTunnelFlags);
    }

    public bool HasLatestVersion => LatestVersion is not null;
    public string LatestVersionText => LatestVersion is null ? "" : _text.Format("Dsh_LatestVersion", LatestVersion);
    public bool HasServers => Servers.Count > 0;
    public bool CanStartLocal => !PageBusy && LocalState is DshRunState.Stopped or DshRunState.External;
    public bool CanStopLocal => !PageBusy && LocalState is DshRunState.Running or DshRunState.Starting;
    public bool HasLocalPanelUrl => LocalPanelUrl is not null;

    private DshRunState LocalState { get; set; } = DshRunState.Stopped;

    partial void OnPageBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartLocal));
        OnPropertyChanged(nameof(CanStopLocal));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>由 MainViewModel 在设置加载后调用。</summary>
    public async Task LoadAsync(int localPort)
    {
        LocalPortText = localPort.ToString();
        ReplaceServers(await _store.ListDshServersAsync());
        ApplyLocalSnapshot();
        await RefreshLatestVersionAsync();
    }

    public void RefreshLocalization()
    {
        ApplyLocalSnapshot();
        foreach (var item in Servers) item.RefreshLocalization(_text);
        OnPropertyChanged(nameof(LatestVersionText));
    }

    /// <summary>本机端口改动同步回设置（模式对齐 Proxy.ScopePersisted；保存随设置页落库）。</summary>
    public event Action<int>? LocalPortApplied;

    [RelayCommand(CanExecute = nameof(CanPageRefresh))]
    public async Task RefreshAsync()
    {
        PageBusy = true;
        try
        {
            await _local.RefreshAsync();
            ApplyLocalSnapshot();
            await RefreshLatestVersionAsync();
        }
        finally
        {
            PageBusy = false;
        }
    }

    private bool CanPageRefresh() => !PageBusy;

    [RelayCommand]
    public async Task StartLocalAsync()
    {
        if (!TryGetLocalPort(out var port))
        {
            AppendLog(_text["Dsh_MessageInvalidPort"]);
            return;
        }

        PageBusy = true;
        AppendLog($"$ dsh web --port {port} --no-open");
        try
        {
            LocalPortApplied?.Invoke(port);
            await _local.StartAsync(port);
            AppendLog($"[{_text["Dsh_Local"]}] {_text["Dsh_LogStarted"]}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            PageBusy = false;
        }
    }

    [RelayCommand]
    public async Task StopLocalAsync()
    {
        PageBusy = true;
        try
        {
            await _local.StopAsync();
            AppendLog($"[{_text["Dsh_Local"]}] {_text["Dsh_LogStopped"]}");
        }
        finally
        {
            PageBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestartLocalAsync()
    {
        if (!TryGetLocalPort(out var port))
        {
            AppendLog(_text["Dsh_MessageInvalidPort"]);
            return;
        }

        PageBusy = true;
        try
        {
            await _local.StopAsync();
            LocalPortApplied?.Invoke(port);
            await _local.StartAsync(port);
            AppendLog($"[{_text["Dsh_Local"]}] {_text["Dsh_LogRestarted"]}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            PageBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLocalPanel()
    {
        if (LocalPanelUrl is not null) OpenInBrowser(LocalPanelUrl);
    }

    public async Task SaveServerAsync(DshServerDefinition server, string? password)
    {
        await _store.SaveDshServerAsync(server, password);
        // 连接与隧道按旧配置建立，保存后一律弃用，下次操作按新配置重来
        _tunnels.Close(server.Id);
        _remoteFactory.DropConnection(server.Id);
        await ReloadServersAsync();
    }

    public async Task DeleteServerAsync(string serverId)
    {
        _tunnels.Close(serverId);
        _remoteFactory.DropConnection(serverId);
        await _store.DeleteDshServerAsync(serverId);
        await ReloadServersAsync();
    }

    /// <summary>把 ~/.ssh/config 条目导入为服务器记录；host+port+user 相同的已有记录跳过。</summary>
    public async Task<(int Imported, int Skipped)> ImportServersAsync(IEnumerable<SshConfigEntry> entries)
    {
        var existing = await _store.ListDshServersAsync();
        var usedLocalPorts = existing.Select(x => x.LocalPort).ToHashSet();
        var imported = 0;
        var skipped = 0;
        foreach (var entry in entries)
        {
            if (existing.Any(x => string.Equals(x.Host, entry.HostName, StringComparison.OrdinalIgnoreCase)
                && x.SshPort == entry.Port
                && string.Equals(x.UserName, entry.UserName, StringComparison.Ordinal)))
            {
                skipped++;
                continue;
            }

            var localPort = Enumerable.Range(LocalForwardPortBase, 2000).First(port => !usedLocalPorts.Contains(port));
            usedLocalPorts.Add(localPort);
            await _store.SaveDshServerAsync(new DshServerDefinition
            {
                Name = entry.Alias,
                Host = entry.HostName,
                SshPort = entry.Port,
                UserName = entry.UserName,
                AuthKind = DshAuthKind.Key,
                KeyPath = entry.IdentityFile,
                RemotePort = 3080,
                LocalPort = localPort
            }, null);
            imported++;
        }

        if (imported > 0) await ReloadServersAsync();
        return (imported, skipped);
    }

    public async Task ProbeAsync(DshServerItemViewModel item)
    {
        item.BeginBusy();
        try
        {
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogProbing"]}");
            var result = await _remoteFactory.Create(item.Server).ProbeAsync();
            item.ApplyProbe(result, _text);
            AppendLog(BuildProbeLog(item, result));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    public Task InstallAsync(DshServerItemViewModel item) => RunWithOutputAsync(item, install: true);

    public Task UpdateAsync(DshServerItemViewModel item) => RunWithOutputAsync(item, install: false);

    public async Task StartRemoteAsync(DshServerItemViewModel item)
    {
        item.BeginBusy();
        try
        {
            await AutoSyncConfigAsync(item);
            AppendLog($"[{item.DisplayName}] $ dsh web --port {item.Server.RemotePort} --no-open");
            var url = await _remoteFactory.Create(item.Server)
                .StartAsync(new DshLaunchOptions(item.Server.RemotePort, item.Server.LocalPort));
            if (url is null)
            {
                AppendLog($"[{item.DisplayName}] {_text["Dsh_LogStartNoUrl"]}");
            }
            else
            {
                item.IsRunning = true;
                AppendLog($"[{item.DisplayName}] {_text["Dsh_LogStarted"]}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    public async Task StopRemoteAsync(DshServerItemViewModel item)
    {
        item.BeginBusy();
        try
        {
            await _remoteFactory.Create(item.Server).StopAsync();
            item.IsRunning = false;
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogStopped"]}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    /// <summary>转发并打开：确保远端运行 → 建隧道 → 拼本地 token 地址 → 打开浏览器。</summary>
    public async Task OpenTunnelAsync(DshServerItemViewModel item)
    {
        item.BeginBusy();
        try
        {
            await AutoSyncConfigAsync(item);
            var service = _remoteFactory.Create(item.Server);
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogProbing"]}");
            var probe = await service.ProbeAsync();
            if (!probe.Reachable)
            {
                item.ApplyProbe(probe, _text);
                throw new InvalidOperationException(probe.Error ?? _text["Dsh_LogUnreachable"]);
            }

            item.ApplyProbe(probe, _text);
            if (probe.InstalledVersion is null)
            {
                throw new InvalidOperationException(_text["Dsh_MessageNotInstalled"]);
            }

            string? panelUrl;
            if (!probe.WebRunning)
            {
                AppendLog($"[{item.DisplayName}] $ dsh web --port {item.Server.RemotePort} --no-open");
                panelUrl = await service.StartAsync(new DshLaunchOptions(item.Server.RemotePort, item.Server.LocalPort));
                if (panelUrl is null) throw new InvalidOperationException(_text["Dsh_LogStartNoUrl"]);
            }
            else
            {
                panelUrl = await service.GetPanelUrlAsync();
                if (panelUrl is null)
                {
                    // web 在运行但不是本应用拉起（日志里没有凭证）：托管重启一次以获取 token
                    AppendLog($"[{item.DisplayName}] {_text["Dsh_LogRestartExternal"]}");
                    await service.StopAsync();
                    panelUrl = await service.StartAsync(new DshLaunchOptions(item.Server.RemotePort, item.Server.LocalPort));
                    if (panelUrl is null) throw new InvalidOperationException(_text["Dsh_LogStartNoUrl"]);
                }
            }

            var handle = await _tunnels.OpenAsync(item.Server);
            item.IsRunning = true;
            item.IsTunneled = true;
            item.LocalPanelUrl = BuildLocalUrl(handle.LocalPort, panelUrl);
            AppendLog($"[{item.DisplayName}] {_text.Format("Dsh_LogTunnel", handle.LocalPort, item.Server.RemotePort)}");
            OpenInBrowser(item.LocalPanelUrl);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    public async Task CloseTunnelAsync(DshServerItemViewModel item)
    {
        item.BeginBusy();
        try
        {
            _tunnels.Close(item.Server.Id);
            item.IsTunneled = false;
            item.LocalPanelUrl = null;
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogTunnelClosed"]}");
        }
        finally
        {
            item.EndBusy();
        }
    }

    /// <summary>手动同步：按对话框勾选推送配置，并把选择持久化到服务器记录（此后自动同步）。</summary>
    public async Task SyncConfigAsync(DshServerItemViewModel item, IReadOnlyList<string> selectedIds)
    {
        item.BeginBusy();
        try
        {
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogSyncing"]}");
            await _configSync.SyncAsync(item.Server, selectedIds, new Progress<string>(AppendLog));
            item.Server.ConfigSyncSelection = string.Join(',', selectedIds);
            await _store.SaveDshServerAsync(item.Server, null);
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogSynced"]}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    /// <summary>打开/启动前按服务器记录的选择自动推送；失败仅记日志，不阻断主流程。</summary>
    private async Task AutoSyncConfigAsync(DshServerItemViewModel item)
    {
        var selection = item.Server.ConfigSyncSelection;
        if (string.IsNullOrWhiteSpace(selection)) return;
        var ids = selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return;
        try
        {
            AppendLog($"[{item.DisplayName}] {_text["Dsh_LogAutoSyncing"]}");
            await _configSync.SyncAsync(item.Server, ids, new Progress<string>(AppendLog));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
    }

    /// <summary>对话框“测试连接”：用表单里尚未落库的密码直接探测（凭据覆盖）。</summary>
    public Task<DshProbeResult> TestConnectionAsync(DshServerDefinition server, string? password) =>
        _remoteFactory.Create(server, new DshConnectCredential(password)).ProbeAsync();

    private async Task RunWithOutputAsync(DshServerItemViewModel item, bool install)
    {
        item.BeginBusy();
        AppendLog($"[{item.DisplayName}] {(install ? _text["Dsh_Install"] : _text["Dsh_Update"])}: npm install -g @deepseek-ai/dsh@latest");
        var progress = new Progress<string>(AppendLog);
        try
        {
            var service = _remoteFactory.Create(item.Server);
            if (install) await service.InstallAsync(progress);
            else await service.UpdateAsync(progress);
            var probe = await service.ProbeAsync();
            item.ApplyProbe(probe, _text);
            AppendLog(BuildProbeLog(item, probe));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppendLog(string.Format(_text["Dsh_LogFailed"], exception.Message));
        }
        finally
        {
            item.EndBusy();
        }
    }

    private async Task RefreshLatestVersionAsync() => LatestVersion = await _versions.GetLatestVersionAsync();

    private async Task ReloadServersAsync() => ReplaceServers(await _store.ListDshServersAsync());

    private void ReplaceServers(IReadOnlyList<DshServerDefinition> servers)
    {
        Servers.Clear();
        foreach (var server in servers) Servers.Add(new DshServerItemViewModel(this, server, _text));
        OnPropertyChanged(nameof(HasServers));
    }

    private void RefreshTunnelFlags()
    {
        foreach (var item in Servers)
        {
            item.IsTunneled = _tunnels.IsOpen(item.Server.Id);
            if (!item.IsTunneled) item.LocalPanelUrl = null;
        }
    }

    private void ApplyLocalSnapshot()
    {
        var snapshot = _local.Snapshot;
        LocalState = snapshot.State;
        LocalHasError = snapshot.Error is not null;
        LocalStateText = snapshot.State switch
        {
            DshRunState.Running => _text["Dsh_StateRunning"],
            DshRunState.Starting => _text["Dsh_StateStarting"],
            DshRunState.External => _text["Dsh_StateExternal"],
            _ => _text["Dsh_StateStopped"]
        };
        if (snapshot.Error is not null) LocalStateText = $"{LocalStateText}：{snapshot.Error}";
        LocalVersionText = snapshot.Version ?? _text["Dsh_NotInstalled"];
        LocalPanelUrl = snapshot.PanelUrl;
        OnPropertyChanged(nameof(CanStartLocal));
        OnPropertyChanged(nameof(CanStopLocal));
        OnPropertyChanged(nameof(HasLocalPanelUrl));
    }

    private bool TryGetLocalPort(out int port) =>
        int.TryParse(LocalPortText.Trim(), out port) && port is >= 1 and <= 65535;

    private string BuildProbeLog(DshServerItemViewModel item, DshProbeResult result)
    {
        if (!result.Reachable)
        {
            return $"[{item.DisplayName}] {_text["Dsh_LogUnreachable"]}：{result.Error}";
        }

        var version = result.InstalledVersion ?? _text["Dsh_NotInstalled"];
        var state = result.WebRunning ? _text["Dsh_StateRunning"] : _text["Dsh_StateStopped"];
        return $"[{item.DisplayName}] {_text["Dsh_Version"]}: {version} · {state}";
    }

    private void AppendLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

    private static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string BuildLocalUrl(int localPort, string remoteUrl) =>
        $"http://127.0.0.1:{localPort}{new Uri(remoteUrl).PathAndQuery}";
}

/// <summary>远端服务器列表中的一行。</summary>
public sealed partial class DshServerItemViewModel : ObservableObject
{
    private readonly DshViewModel _parent;
    private LocalizationService _text;

    public DshServerItemViewModel(DshViewModel parent, DshServerDefinition server, LocalizationService text)
    {
        _parent = parent;
        _text = text;
        Server = server;
        VersionText = text["Dsh_NotProbed"];
    }

    public DshServerDefinition Server { get; private set; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isTunneled;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _localPanelUrl;

    public string DisplayName => string.IsNullOrWhiteSpace(Server.Name) ? Server.Host : Server.Name;
    public string HostText => $"{Server.UserName}@{Server.Host}:{Server.SshPort}";
    public string PortsText => _text.Format("Dsh_Ports", Server.RemotePort, Server.LocalPort);
    public bool ShowStatusError => !string.IsNullOrEmpty(StatusText);

    partial void OnIsBusyChanged(bool value)
    {
        ProbeCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        StartRemoteCommand.NotifyCanExecuteChanged();
        StopRemoteCommand.NotifyCanExecuteChanged();
        OpenTunnelCommand.NotifyCanExecuteChanged();
        CloseTunnelCommand.NotifyCanExecuteChanged();
    }

    public void UpdateServer(DshServerDefinition server)
    {
        Server = server;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(PortsText));
    }

    public void RefreshLocalization(LocalizationService text)
    {
        _text = text;
        OnPropertyChanged(nameof(PortsText));
    }

    public void BeginBusy()
    {
        IsBusy = true;
        StatusText = "";
    }

    public void EndBusy() => IsBusy = false;

    public void ApplyProbe(DshProbeResult result, LocalizationService text)
    {
        if (!result.Reachable)
        {
            IsRunning = false;
            StatusText = result.Error ?? "";
            return;
        }

        // 可达但 dsh 跑不起来（如 Node 过旧）时，把原因显示在状态栏
        StatusText = result.Error ?? "";
        IsRunning = result.WebRunning;
        VersionText = result.InstalledVersion ?? text["Dsh_NotInstalled"];
    }

    private bool CanOperate => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task Probe() => _parent.ProbeAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task Install() => _parent.InstallAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task Update() => _parent.UpdateAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task StartRemote() => _parent.StartRemoteAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task StopRemote() => _parent.StopRemoteAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task OpenTunnel() => _parent.OpenTunnelAsync(this);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task CloseTunnel() => _parent.CloseTunnelAsync(this);
}
