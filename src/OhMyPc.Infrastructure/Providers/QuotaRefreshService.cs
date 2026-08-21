using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Providers;

public sealed class QuotaRefreshService(
    IAppStore store,
    IEnumerable<IQuotaProvider> providers,
    IAutomationEventPublisher eventPublisher,
    ITextLocalizer text,
    ILogger<QuotaRefreshService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event EventHandler? Refreshed;

    public async Task RefreshAllAsync(bool onlyDue = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sources = await store.ListDataSourcesAsync(cancellationToken);
            foreach (var source in sources.Where(x => x.Enabled))
            {
                if (onlyDue && source.LastAttemptAt is not null
                    && DateTimeOffset.UtcNow - source.LastAttemptAt.Value < TimeSpan.FromSeconds(source.PollIntervalSeconds)) continue;
                await RefreshCoreAsync(source, cancellationToken);
            }
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = await store.GetDataSourceAsync(sourceId, cancellationToken);
            if (source is not null) await RefreshCoreAsync(source, cancellationToken);
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshCoreAsync(DataSourceDefinition source, CancellationToken cancellationToken)
    {
        var previousStatus = source.Status;
        source.LastAttemptAt = DateTimeOffset.UtcNow;
        var apiKey = await store.GetCredentialAsync(source.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            source.Status = ProviderStatus.AuthenticationFailed;
            source.LastError = "API key is missing";
            await store.UpdateDataSourceHealthAsync(source, cancellationToken);
            if (source.Status != previousStatus)
            {
                await PublishProviderStatusChangedAsync(source, previousStatus, cancellationToken);
            }
            return;
        }

        QuotaPollResult result;
        try
        {
            result = await providers.Single(x => x.Kind == source.Kind).PollAsync(source, apiKey, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            result = new QuotaPollResult { Status = ProviderStatus.Unavailable, Error = exception.Message };
        }

        if (result.Status == ProviderStatus.Healthy)
        {
            var snapshots = result.Snapshots.ToList();
            if (source.Kind == DataSourceKind.NewApi)
            {
                var previousBalance = (await store.ListCurrentQuotasAsync(cancellationToken))
                    .SingleOrDefault(x => x.SourceId == source.Id && x.WindowKey == "balance");
                ApplyNewApiBalanceProgressLimit(
                    snapshots.Single(x => x.WindowKey == "balance"),
                    previousBalance);
            }

            source.Status = ProviderStatus.Healthy;
            source.ConsecutiveFailures = 0;
            source.LastSuccessAt = DateTimeOffset.UtcNow;
            source.LastError = null;
            await store.ReplaceCurrentQuotasAsync(source.Id, snapshots, cancellationToken);
            await PublishQuotasAsync(source, snapshots, cancellationToken);
        }
        else
        {
            source.ConsecutiveFailures++;
            source.LastError = result.Error;
            if (result.Status == ProviderStatus.AuthenticationFailed || source.ConsecutiveFailures >= 2)
            {
                source.Status = result.Status;
            }
        }

        await store.UpdateDataSourceHealthAsync(source, cancellationToken);
        if (source.Status != previousStatus)
        {
            await PublishProviderStatusChangedAsync(source, previousStatus, cancellationToken);
        }
        logger.LogInformation("Quota source {Source} refreshed with status {Status}", source.Name, source.Status);
    }

    internal static void ApplyNewApiBalanceProgressLimit(QuotaSnapshot current, QuotaSnapshot? previous)
    {
        var remaining = current.Remaining!.Value;
        if (previous?.ProgressLimit is null)
        {
            current.ProgressLimit = remaining;
            return;
        }

        const double changeThreshold = 0.000001;
        var allocationIncreased = current.Limit!.Value > previous.Limit!.Value + changeThreshold;
        var balanceIncreased = remaining > previous.Remaining!.Value + changeThreshold;
        current.ProgressLimit = allocationIncreased || balanceIncreased
            ? remaining
            : previous.ProgressLimit;
    }

    private async Task PublishQuotasAsync(
        DataSourceDefinition source,
        IReadOnlyCollection<QuotaSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            var fields = new JsonObject
            {
                ["sourceId"] = source.Id,
                ["windowKey"] = snapshot.WindowKey,
                ["used"] = snapshot.Used,
                ["unit"] = snapshot.Unit
            };
            if (snapshot.Remaining is not null) fields["remaining"] = snapshot.Remaining.Value;
            if (snapshot.RemainingPercent is not null) fields["remainingPercent"] = snapshot.RemainingPercent.Value;

            var unit = snapshot.WindowKey == "zhipu-mcp-monthly" ? text["Quota_CallsUnit"] : snapshot.Unit;
            var percentText = snapshot.RemainingPercent is null
                ? ""
                : text.Format("Notification_PercentPart", snapshot.RemainingPercent);
            var resetText = snapshot.ResetAt is null
                ? ""
                : text.Format("Notification_ResetPart", snapshot.ResetAt.Value.ToLocalTime());

            await eventPublisher.PublishAsync(new AutomationEvent
            {
                Type = AutomationEventTypes.QuotaObserved,
                SourceId = source.Id,
                SubjectKey = $"{source.Id}:quota:{snapshot.WindowKey}",
                OccurredAt = snapshot.ObservedAt,
                Title = source.Name,
                Body = text.Format(
                    "Notification_QuotaBody",
                    QuotaLabel(snapshot),
                    snapshot.Remaining,
                    unit,
                    percentText,
                    resetText),
                Fields = fields
            }, cancellationToken);
        }
    }

    private Task PublishProviderStatusChangedAsync(
        DataSourceDefinition source,
        ProviderStatus previousStatus,
        CancellationToken cancellationToken) =>
        eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.ProviderStatusChanged,
            SourceId = source.Id,
            SubjectKey = $"{source.Id}:status",
            Title = source.Name,
            Body = text.Format("Notification_StatusChanged", text.GetEnum(previousStatus), text.GetEnum(source.Status)),
            Fields = new JsonObject
            {
                ["sourceId"] = source.Id,
                ["previousStatus"] = previousStatus.ToString(),
                ["currentStatus"] = source.Status.ToString()
            }
        }, cancellationToken);

    private string QuotaLabel(QuotaSnapshot snapshot) => snapshot.WindowKey.ToLowerInvariant() switch
    {
        "total" => text["Quota_Total"],
        "daily" => text["Quota_Daily"],
        "weekly" => text["Quota_Weekly"],
        "monthly" => text["Quota_Monthly"],
        "billing" => text["Quota_Billing"],
        "balance" => text["Quota_Balance"],
        _ => snapshot.Label
    };
}
