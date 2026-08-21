using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Providers;

public sealed class Sub2ApiProvider(IHttpClientFactory clientFactory) : IQuotaProvider
{
    public DataSourceKind Kind => DataSourceKind.Sub2Api;

    public async Task<QuotaPollResult> PollAsync(DataSourceDefinition source, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}/usage?days=90");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await clientFactory.CreateClient("providers").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return Failure(response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var observedAt = DateTimeOffset.UtcNow;
        var unit = ProviderJson.Text(root, "unit", "USD");
        var snapshots = new List<QuotaSnapshot>();

        if (root.TryGetProperty("quota", out var quota) && quota.ValueKind == JsonValueKind.Object)
        {
            Add(snapshots, source, "total", "Total quota", quota, unit, observedAt, null);
        }

        if (root.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Array)
        {
            foreach (var window in rateLimits.EnumerateArray())
            {
                var key = ProviderJson.Text(window, "window", "window");
                Add(snapshots, source, key, key.ToUpperInvariant(), window, unit, observedAt, ProviderJson.Date(window, "reset_at"));
            }
        }

        if (root.TryGetProperty("subscription", out var subscription) && subscription.ValueKind == JsonValueKind.Object)
        {
            AddSubscriptionWindow(snapshots, source, subscription, "daily", "Daily", observedAt, NextDay());
            AddSubscriptionWindow(snapshots, source, subscription, "weekly", "Weekly", observedAt, WeeklyReset(subscription));
            AddSubscriptionWindow(snapshots, source, subscription, "monthly", "Monthly", observedAt, NextMonth());
        }

        if (snapshots.Count == 0 && root.TryGetProperty("remaining", out var remaining) && remaining.TryGetDouble(out var balance))
        {
            snapshots.Add(new QuotaSnapshot
            {
                SourceId = source.Id,
                SourceName = source.Name,
                WindowKey = "balance",
                Label = ProviderJson.Text(root, "planName", "Balance"),
                Remaining = balance,
                Unit = unit,
                ObservedAt = observedAt
            });
        }

        return new QuotaPollResult { Snapshots = snapshots, Status = ProviderStatus.Healthy };
    }

    private static void Add(
        ICollection<QuotaSnapshot> snapshots,
        DataSourceDefinition source,
        string key,
        string label,
        JsonElement value,
        string unit,
        DateTimeOffset observedAt,
        DateTimeOffset? resetAt)
    {
        var limit = ProviderJson.Number(value, "limit");
        if (limit <= 0) return;
        var used = ProviderJson.Number(value, "used");
        var remaining = value.TryGetProperty("remaining", out _) ? ProviderJson.Number(value, "remaining") : Math.Max(0, limit - used);
        snapshots.Add(new QuotaSnapshot
        {
            SourceId = source.Id,
            SourceName = source.Name,
            WindowKey = key,
            Label = label,
            Used = used,
            Limit = limit,
            Remaining = remaining,
            Unit = unit,
            ResetAt = resetAt,
            ObservedAt = observedAt
        });
    }

    private static void AddSubscriptionWindow(
        ICollection<QuotaSnapshot> snapshots,
        DataSourceDefinition source,
        JsonElement subscription,
        string key,
        string label,
        DateTimeOffset observedAt,
        DateTimeOffset? resetAt)
    {
        var limit = ProviderJson.Number(subscription, $"{key}_limit_usd");
        if (limit <= 0) return;
        var used = ProviderJson.Number(subscription, $"{key}_usage_usd");
        snapshots.Add(new QuotaSnapshot
        {
            SourceId = source.Id,
            SourceName = source.Name,
            WindowKey = key,
            Label = label,
            Used = used,
            Limit = limit,
            Remaining = Math.Max(0, limit - used),
            Unit = "USD",
            ResetAt = resetAt,
            ObservedAt = observedAt,
            Detail = ProviderJson.Text(subscription, "expires_at")
        });
    }

    private static QuotaPollResult Failure(HttpStatusCode statusCode) => new()
    {
        Status = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ProviderStatus.AuthenticationFailed
            : ProviderStatus.Unavailable,
        Error = $"HTTP {(int)statusCode}"
    };

    private static DateTimeOffset NextDay()
    {
        var now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);
    }

    private static DateTimeOffset NextMonth()
    {
        var now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(1);
    }

    private static DateTimeOffset? WeeklyReset(JsonElement subscription)
    {
        var start = ProviderJson.Date(subscription, "weekly_window_start");
        return start?.AddDays(7);
    }
}
