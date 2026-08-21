using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class LocalUsageWorker(
    LocalToolDetector detector,
    LocalUsageRefreshService refreshService,
    ILogger<LocalUsageWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumChangeDebounce = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinimumChangeRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(15);
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly List<FileSystemWatcher> _watchers = [];
    private long _lastRefreshTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConfigureWatchers();
        await RefreshSafeAsync(fullHistory: true, stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(
            WatchChangesAsync(stoppingToken),
            RefreshFullHistoryPeriodicallyAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        while (await _changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DrainChanges();
            var debounceStarted = Stopwatch.GetTimestamp();
            bool receivedMoreChanges;
            do
            {
                await Task.Delay(ChangeDebounce, cancellationToken).ConfigureAwait(false);
                receivedMoreChanges = DrainChanges();
            } while (receivedMoreChanges
                && Stopwatch.GetElapsedTime(debounceStarted) < MaximumChangeDebounce);

            await WaitForMinimumRefreshIntervalAsync(cancellationToken).ConfigureAwait(false);
            DrainChanges();
            await RefreshSafeAsync(fullHistory: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshFullHistoryPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FullRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshSafeAsync(fullHistory: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForMinimumRefreshIntervalAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _lastRefreshTimestamp) is var lastRefresh && lastRefresh != 0)
        {
            var remaining = MinimumChangeRefreshInterval - Stopwatch.GetElapsedTime(lastRefresh);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool DrainChanges()
    {
        var changed = false;
        while (_changes.Reader.TryRead(out _)) changed = true;
        return changed;
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        base.Dispose();
    }

    private void ConfigureWatchers()
    {
        foreach (var root in detector.GetWatchRoots())
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            _watchers.Add(watcher);
        }
        logger.LogInformation("正在监控 {Count} 个本地 AI 数据目录", _watchers.Count);
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => _changes.Writer.TryWrite(1);

    private async Task RefreshSafeAsync(bool fullHistory, CancellationToken cancellationToken)
    {
        try
        {
            await refreshService.RefreshAsync(fullHistory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "本地用量刷新失败");
        }
        finally
        {
            Volatile.Write(ref _lastRefreshTimestamp, Stopwatch.GetTimestamp());
        }
    }
}
