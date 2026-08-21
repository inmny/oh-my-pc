using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Services;

public sealed class DesktopNotificationSink(
    INotificationFeed notifications,
    ILogger<DesktopNotificationSink> logger) : IDisposable
{
    private bool _started;

    public event EventHandler<NotificationRecord>? Published;

    public void Start()
    {
        if (_started) return;
        _started = true;
        notifications.Published += Notifications_Published;
    }

    public void Replay(NotificationRecord notification) => Queue(notification);

    private void Notifications_Published(object? sender, NotificationRecord notification) => Queue(notification);

    private void Queue(NotificationRecord notification)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        try
        {
            _ = dispatcher.BeginInvoke(
                () => Dispatch(notification),
                System.Windows.Threading.DispatcherPriority.Normal);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogDebug(exception, "界面关闭期间忽略通知 {NotificationId} 的桌面投递", notification.Id);
        }
    }

    private void Dispatch(NotificationRecord notification)
    {
        foreach (EventHandler<NotificationRecord> handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知 {NotificationId} 的桌面订阅者执行失败", notification.Id);
            }
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        notifications.Published -= Notifications_Published;
        _started = false;
    }
}
