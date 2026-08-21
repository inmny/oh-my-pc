using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Notifications;

public sealed class NotificationCenterService(
    IAppStore store,
    ILogger<NotificationCenterService> logger) : INotificationSink, INotificationFeed, IDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private DateTimeOffset _lastPruneAttempt = DateTimeOffset.MinValue;

    public event EventHandler<NotificationRecord>? Published;

    public async Task<NotificationRecord> PublishAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        NotificationRecord record;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            record = new NotificationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Origin = message.Origin,
                Source = string.IsNullOrWhiteSpace(message.Source) ? "oh-my-pc" : message.Source.Trim(),
                Title = message.Title,
                Body = message.Body,
                Channels = message.Channels,
                Severity = message.Severity,
                SubjectKey = message.SubjectKey,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await store.SaveNotificationAsync(record, cancellationToken);
            await PruneIfDueAsync(record.CreatedAt);
        }
        finally
        {
            _writeGate.Release();
        }

        foreach (EventHandler<NotificationRecord> handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, record);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知 {NotificationId} 的实时事件订阅者执行失败", record.Id);
            }
        }

        return record;
    }

    private async Task PruneIfDueAsync(DateTimeOffset now)
    {
        if (now - _lastPruneAttempt < TimeSpan.FromHours(1)) return;
        _lastPruneAttempt = now;
        try
        {
            var settings = await store.GetSettingsAsync(CancellationToken.None);
            var retentionDays = NotificationRetentionPolicy.Normalize(settings.NotificationHistoryRetentionDays);
            await store.PruneNotificationsAsync(now.AddDays(-retentionDays), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "运行期间清理通知历史失败，稍后重试");
            _lastPruneAttempt = now.AddMinutes(-55);
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        RunMaintenanceAsync(() => store.DeleteNotificationAsync(id, cancellationToken), cancellationToken);

    public Task<int> ClearThroughAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        RunMaintenanceAsync(() => store.DeleteNotificationsThroughAsync(cutoff, cancellationToken), cancellationToken);

    public Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        RunMaintenanceAsync(() => store.PruneNotificationsAsync(cutoff, cancellationToken), cancellationToken);

    private async Task RunMaintenanceAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T> RunMaintenanceAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
