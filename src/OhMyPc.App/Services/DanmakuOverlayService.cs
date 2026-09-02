using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Services;

/// <summary>
/// 弹幕桌面投递：用户不在电脑前（空闲超时或锁屏）时把消息积压到 <see cref="DanmakuBacklog"/>，
/// 回来后按（来源,标题）合并逐条补播；补播中途再次离开则停止播放并把剩余条目放回积压。
/// </summary>
public sealed class DanmakuOverlayService(
    DesktopNotificationSink notifications,
    IAppStore store,
    IUserPresenceService presence,
    ITextLocalizer text,
    ILogger<DanmakuOverlayService> logger) : IDisposable
{
    private static readonly TimeSpan PlaybackInterval = TimeSpan.FromMilliseconds(1500);

    private readonly HashSet<DanmakuOverlayWindow> _windows = [];
    private readonly DanmakuBacklog _backlog = new();
    private readonly Queue<DanmakuBacklogEntry> _playback = [];
    private DispatcherTimer? _playbackTimer;
    private int _nextLane;

    public void Start()
    {
        notifications.Published += Notifications_Published;
        presence.StateChanged += Presence_StateChanged;
    }

    private async void Notifications_Published(object? sender, NotificationRecord message)
    {
        if (!message.Channels.HasFlag(NotificationChannels.Danmaku)) return;
        try
        {
            var settings = await store.GetSettingsAsync();
            if (!settings.DanmakuEnabled) return;

            if (settings.DanmakuHoldWhenAway && presence.IsAway && _playbackTimer is null)
            {
                _backlog.Add(message, DateTimeOffset.Now);
                return;
            }

            Show(message, settings);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "弹幕通知 {NotificationId} 显示失败", message.Id);
        }
    }

    private void Presence_StateChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(OnPresenceChanged);
    }

    private void OnPresenceChanged()
    {
        if (presence.IsAway)
        {
            PausePlayback();
            return;
        }

        if (_playbackTimer is not null) return;
        var drained = _backlog.Drain();
        if (drained.Entries.Count == 0)
        {
            // 条目全部在补播中途放回后又被挤掉的场景：只剩丢弃计数，仅弹一条摘要
            if (drained.DroppedCount > 0)
            {
                _playback.Enqueue(SummaryEntry(drained.DroppedCount));
                StartPlaybackTimer();
            }
            return;
        }

        foreach (var entry in drained.Entries) _playback.Enqueue(entry);
        if (drained.DroppedCount > 0) _playback.Enqueue(SummaryEntry(drained.DroppedCount));
        StartPlaybackTimer();
    }

    private void StartPlaybackTimer()
    {
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PlaybackInterval };
        _playbackTimer.Tick += (_, _) => _ = PlayNextAsync();
        _ = PlayNextAsync();
        _playbackTimer.Start();
    }

    /// <summary>补播中途用户再次离开：停止播放，剩余条目原样放回积压。</summary>
    private void PausePlayback()
    {
        if (_playbackTimer is null) return;
        _playbackTimer.Stop();
        _playbackTimer = null;
        while (_playback.Count > 0) _backlog.Restore(_playback.Dequeue());
    }

    private async Task PlayNextAsync()
    {
        if (_playback.Count == 0)
        {
            if (_playbackTimer is not null)
            {
                _playbackTimer.Stop();
                _playbackTimer = null;
            }
            return;
        }

        var entry = _playback.Dequeue();
        try
        {
            var settings = await store.GetSettingsAsync();
            if (settings.DanmakuEnabled) Show(FormatForPlayback(entry), settings);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "补播弹幕 {NotificationId} 显示失败", entry.Latest.Id);
        }
    }

    private NotificationRecord FormatForPlayback(DanmakuBacklogEntry entry)
    {
        if (entry.Count <= 1) return entry.Latest;
        var latest = entry.Latest;
        return new NotificationRecord
        {
            Id = latest.Id,
            Origin = latest.Origin,
            Source = latest.Source,
            Title = latest.Title,
            Body = $"{latest.Body}\n{text.Format("Danmaku_BacklogCountFormat", entry.Count)}",
            Channels = latest.Channels,
            Severity = latest.Severity,
            SubjectKey = latest.SubjectKey,
            CreatedAt = latest.CreatedAt
        };
    }

    private DanmakuBacklogEntry SummaryEntry(int droppedCount) => new()
    {
        Latest = new NotificationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Origin = NotificationOrigin.Application,
            Source = "danmaku-backlog",
            Title = text["Danmaku_BacklogSummaryTitle"],
            Body = text.Format("Danmaku_BacklogSummaryFormat", droppedCount),
            Channels = NotificationChannels.Danmaku,
            Severity = NotificationSeverity.Info,
            CreatedAt = DateTimeOffset.Now
        },
        Count = 1,
        FirstAt = DateTimeOffset.Now,
        LastAt = DateTimeOffset.Now
    };

    private void Show(NotificationRecord message, AppSettings settings)
    {
        var window = new DanmakuOverlayWindow(message, settings, _nextLane++ % 7);
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Show();
    }

    public void Dispose()
    {
        notifications.Published -= Notifications_Published;
        presence.StateChanged -= Presence_StateChanged;
        _playbackTimer?.Stop();
        _playbackTimer = null;
        _playback.Clear();
        foreach (var window in _windows.ToArray()) window.Close();
    }
}
