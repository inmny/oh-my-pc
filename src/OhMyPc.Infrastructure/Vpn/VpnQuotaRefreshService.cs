using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Vpn;

public sealed class VpnQuotaRefreshService(
    IAppStore store,
    IVpnQuotaClient client,
    IAutomationEventPublisher eventPublisher,
    ITextLocalizer text,
    ILogger<VpnQuotaRefreshService> logger)
{
    private const string SourceId = "passgo";
    private const double BytesPerGibibyte = 1024d * 1024d * 1024d;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event EventHandler? Refreshed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = await store.GetVpnAccountAsync(cancellationToken);
            if (account is null) return;

            var previousStatus = account.Status;
            account.LastAttemptAt = DateTimeOffset.UtcNow;
            var observedAt = account.LastAttemptAt.Value;
            var succeeded = false;
            try
            {
                var authData = await store.GetVpnAuthDataAsync(cancellationToken);
                var snapshot = await client.GetSubscriptionAsync(authData!, cancellationToken);
                account.Email = snapshot.Email;
                account.PlanName = snapshot.PlanName;
                account.UploadedBytes = snapshot.UploadedBytes;
                account.DownloadedBytes = snapshot.DownloadedBytes;
                account.TransferLimitBytes = snapshot.TransferLimitBytes;
                account.ExpiresAt = snapshot.ExpiresAt;
                account.ResetDay = snapshot.ResetDay;
                account.Status = ProviderStatus.Healthy;
                account.LastSuccessAt = observedAt;
                account.LastError = null;
                succeeded = true;
            }
            catch (PassGoApiException exception)
            {
                account.Status = exception.AuthenticationFailed
                    ? ProviderStatus.AuthenticationFailed
                    : ProviderStatus.Unavailable;
                account.LastError = exception.Message;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException)
            {
                account.Status = ProviderStatus.Unavailable;
                account.LastError = exception.Message;
            }

            await store.SaveVpnAccountAsync(account, cancellationToken: cancellationToken);
            if (succeeded)
            {
                await store.UpsertVpnDailyUsageAsync(new VpnDailyUsagePoint
                {
                    Date = DateOnly.FromDateTime(DateTime.Now),
                    UploadedBytes = account.UploadedBytes,
                    DownloadedBytes = account.DownloadedBytes,
                    TransferLimitBytes = account.TransferLimitBytes,
                    ObservedAt = observedAt
                }, cancellationToken);
                await PublishQuotaObservedAsync(account, observedAt, cancellationToken);
                if (account.ExpiresAt is not null)
                {
                    await PublishExpirationObservedAsync(account, observedAt, cancellationToken);
                }
            }
            if (account.Status != previousStatus)
            {
                await PublishStatusChangedAsync(account, previousStatus, observedAt, cancellationToken);
            }
            logger.LogInformation("VPN quota refreshed with status {Status}", account.Status);
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task PublishQuotaObservedAsync(
        VpnAccountDefinition account,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.VpnQuotaObserved,
            SourceId = SourceId,
            SubjectKey = $"{SourceId}:vpn:quota",
            OccurredAt = observedAt,
            Title = text["Notification_VpnTitle"],
            Body = text.Format(
                "Notification_VpnQuotaBody",
                PlanName(account),
                account.RemainingBytes / BytesPerGibibyte,
                account.RemainingPercent),
            Fields = new JsonObject
            {
                ["email"] = account.Email,
                ["planName"] = PlanName(account),
                ["remainingPercent"] = account.RemainingPercent,
                ["remainingGiB"] = account.RemainingBytes / BytesPerGibibyte,
                ["usedGiB"] = account.UsedBytes / BytesPerGibibyte,
                ["limitGiB"] = account.TransferLimitBytes / BytesPerGibibyte
            }
        }, cancellationToken);

    private Task PublishExpirationObservedAsync(
        VpnAccountDefinition account,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var daysRemaining = DaysRemaining(account.ExpiresAt!.Value, observedAt);
        return eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.VpnExpirationObserved,
            SourceId = SourceId,
            SubjectKey = $"{SourceId}:vpn:expiration",
            OccurredAt = observedAt,
            Title = text["Notification_VpnTitle"],
            Body = daysRemaining <= 0
                ? text.Format(
                    "Notification_VpnExpiredBody",
                    PlanName(account),
                    account.ExpiresAt.Value.ToLocalTime())
                : text.Format(
                    "Notification_VpnExpirationBody",
                    PlanName(account),
                    daysRemaining,
                    account.ExpiresAt.Value.ToLocalTime()),
            Fields = new JsonObject
            {
                ["email"] = account.Email,
                ["planName"] = PlanName(account),
                ["daysRemaining"] = daysRemaining,
                ["expiresAt"] = account.ExpiresAt.Value.ToLocalTime().ToString("O"),
                ["expired"] = daysRemaining <= 0
            }
        }, cancellationToken);
    }

    private Task PublishStatusChangedAsync(
        VpnAccountDefinition account,
        ProviderStatus previousStatus,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.VpnStatusChanged,
            SourceId = SourceId,
            SubjectKey = $"{SourceId}:vpn:status",
            OccurredAt = observedAt,
            Title = text["Notification_VpnTitle"],
            Body = text.Format(
                "Notification_VpnStatusBody",
                text.GetEnum(previousStatus),
                text.GetEnum(account.Status),
                account.LastError is null ? "" : text.Format("Notification_VpnErrorPart", account.LastError)),
            Fields = new JsonObject
            {
                ["email"] = account.Email,
                ["previousStatus"] = previousStatus.ToString(),
                ["currentStatus"] = account.Status.ToString(),
                ["authenticationFailed"] = account.Status == ProviderStatus.AuthenticationFailed,
                ["error"] = account.LastError ?? ""
            }
        }, cancellationToken);

    private string PlanName(VpnAccountDefinition account) =>
        string.IsNullOrWhiteSpace(account.PlanName) ? text["Vpn_NoPlan"] : account.PlanName;

    private static int DaysRemaining(DateTimeOffset expiresAt, DateTimeOffset now) =>
        (int)Math.Ceiling((expiresAt - now).TotalDays);
}
