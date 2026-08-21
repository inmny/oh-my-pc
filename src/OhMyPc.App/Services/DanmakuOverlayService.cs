using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Services;

public sealed class DanmakuOverlayService(
    DesktopNotificationSink notifications,
    IAppStore store,
    ILogger<DanmakuOverlayService> logger) : IDisposable
{
    private readonly HashSet<DanmakuOverlayWindow> _windows = [];
    private int _nextLane;

    public void Start() => notifications.Published += Notifications_Published;

    private async void Notifications_Published(object? sender, NotificationRecord message)
    {
        if (!message.Channels.HasFlag(NotificationChannels.Danmaku)) return;
        try
        {
            var settings = await store.GetSettingsAsync();
            if (!settings.DanmakuEnabled) return;

            var window = new DanmakuOverlayWindow(message, settings, _nextLane++ % 7);
            _windows.Add(window);
            window.Closed += (_, _) => _windows.Remove(window);
            window.Show();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "弹幕通知 {NotificationId} 显示失败", message.Id);
        }
    }

    public void Dispose()
    {
        notifications.Published -= Notifications_Published;
        foreach (var window in _windows.ToArray()) window.Close();
    }
}
