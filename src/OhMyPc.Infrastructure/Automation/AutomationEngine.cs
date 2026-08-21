using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Automation;

public sealed class AutomationEngine(
    IAppStore store,
    AutomationRuleMatcher matcher,
    IEnumerable<IAutomationActionHandler> actionHandlers) : IAutomationEventPublisher
{
    private readonly IReadOnlyDictionary<string, IAutomationActionHandler> _actionHandlers =
        actionHandlers.ToDictionary(handler => handler.Kind);

    public async Task PublishAsync(AutomationEvent automationEvent, CancellationToken cancellationToken = default)
    {
        var rules = await store.ListRulesForEventAsync(automationEvent.Type, cancellationToken);
        if (rules.Count == 0) return;

        var settings = await store.GetSettingsAsync(cancellationToken);
        if (!settings.NotificationsEnabled) return;

        var now = DateTimeOffset.UtcNow;
        var quiet = IsQuietHours(settings, DateTimeOffset.Now);
        foreach (var rule in rules.Where(rule => matcher.Matches(rule, automationEvent)))
        {
            var state = await store.GetRuleStateAsync(rule.Id, automationEvent.SubjectKey, cancellationToken);
            if (state?.LastExecutedAt is not null
                && now - state.LastExecutedAt.Value < TimeSpan.FromMinutes(Math.Max(0, rule.CooldownMinutes)))
            {
                continue;
            }

            if (rule.RespectQuietHours && quiet) continue;

            foreach (var action in rule.Actions)
            {
                await _actionHandlers[action.Kind].ExecuteAsync(action, automationEvent, cancellationToken);
            }

            await store.SaveRuleStateAsync(new AutomationRuleState
            {
                RuleId = rule.Id,
                SubjectKey = automationEvent.SubjectKey,
                LastExecutedAt = now
            }, cancellationToken);
        }
    }

    private static bool IsQuietHours(AppSettings settings, DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(settings.QuietHoursStart, out var start)
            || !TimeOnly.TryParse(settings.QuietHoursEnd, out var end))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        return start <= end ? time >= start && time < end : time >= start || time < end;
    }
}
