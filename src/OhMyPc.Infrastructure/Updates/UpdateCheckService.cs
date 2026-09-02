using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using Velopack;
using Velopack.Sources;

namespace OhMyPc.Infrastructure.Updates;

/// <summary>
/// 周期（1 小时）检查 GitHub Release 是否有新版本；仅在本应用由 Velopack 安装运行时生效，
/// 开发机直接跑 bin 目录会因 IsInstalled=false 跳过。发现新版本走通知管线 + 事件给界面横幅，
/// 由用户确认后下载并自动重启，不做静默更新。
/// </summary>
public sealed class UpdateCheckService(
    IAppStore store,
    INotificationSink notifications,
    ITextLocalizer text,
    ILogger<UpdateCheckService> logger) : IHostedService, IDisposable
{
    private const string RepoUrl = "https://github.com/inmny/oh-my-pc";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    public sealed record UpdateInfo(string TargetVersion);

    /// <summary>发现可用新版本（后台线程触发，界面侧自行调度到 UI 线程）。</summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    /// <summary>下载进度（0-100）。</summary>
    public event EventHandler<int>? DownloadProgress;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = CheckAsync(CancellationToken.None), null, FirstCheckDelay, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await store.GetSettingsAsync(cancellationToken);
            if (!settings.UpdateCheckEnabled) return;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_pending is not null) return;
                var manager = GetManager();
                if (manager is null) return;
                var result = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (result is null) return;
                var info = new UpdateInfo(result.TargetFullRelease.Version.ToString());
                _pending = info;
                logger.LogInformation("发现新版本 {Version}", info.TargetVersion);
                await notifications.PublishAsync(new NotificationMessage
                {
                    Origin = NotificationOrigin.Application,
                    Source = "ompc-update",
                    Title = text["Update_NotificationTitle"],
                    Body = text.Format("Update_NotificationBody", info.TargetVersion),
                    Channels = NotificationChannels.Danmaku | NotificationChannels.Tray,
                    Severity = NotificationSeverity.Info
                }, cancellationToken);
                UpdateAvailable?.Invoke(this, info);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 检查失败不打扰用户，等下一轮周期重试
            logger.LogWarning(exception, "检查应用更新失败");
        }
    }

    /// <summary>下载最新版本并应用更新、自动重启；由界面「立即更新」按钮触发。</summary>
    public async Task DownloadAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var manager = GetManager() ?? throw new InvalidOperationException("当前不是安装版运行，无法自动更新。");
        var result = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (result is null) return;
        await manager.DownloadUpdatesAsync(result, progress => DownloadProgress?.Invoke(this, progress), cancellationToken).ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(result);
    }

    private UpdateManager? GetManager()
    {
        if (_manager is not null) return _manager;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled) return null;
            _manager = manager;
            return manager;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "更新管理器不可用");
            return null;
        }
    }
}
