using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Automation;

public sealed class LocalNotificationActionHandler(INotificationSink notificationSink) : IAutomationActionHandler
{
    public string Kind => AutomationActionKinds.LocalNotification;

    public Task ExecuteAsync(
        AutomationActionDefinition action,
        AutomationEvent automationEvent,
        CancellationToken cancellationToken = default)
    {
        var options = LocalNotificationActionOptions.FromDefinition(action);
        return notificationSink.PublishAsync(new NotificationMessage
        {
            Origin = NotificationOrigin.Automation,
            Source = string.IsNullOrWhiteSpace(automationEvent.SourceId) ? "automation" : automationEvent.SourceId,
            Title = automationEvent.Title,
            Body = automationEvent.Body,
            Channels = options.Channels,
            Severity = options.Severity,
            SubjectKey = automationEvent.SubjectKey
        }, cancellationToken);
    }
}
