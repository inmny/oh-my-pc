using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Providers;

public sealed class NewApiProvider(IHttpClientFactory clientFactory) : IQuotaProvider
{
    public DataSourceKind Kind => DataSourceKind.NewApi;

    public async Task<QuotaPollResult> PollAsync(DataSourceDefinition source, string apiKey, CancellationToken cancellationToken = default)
    {
        var client = clientFactory.CreateClient("providers");
        using var subscriptionRequest = Request($"{source.BaseUrl.TrimEnd('/')}/dashboard/billing/subscription", apiKey);
        using var subscriptionResponse = await client.SendAsync(subscriptionRequest, cancellationToken);
        if (!subscriptionResponse.IsSuccessStatusCode) return Failure(subscriptionResponse.StatusCode);

        using var subscription = JsonDocument.Parse(await subscriptionResponse.Content.ReadAsStringAsync(cancellationToken));
        var limit = ProviderJson.Number(subscription.RootElement, "hard_limit_usd");
        var accessUntil = ProviderJson.Date(subscription.RootElement, "access_until");

        var today = DateTime.Today;
        var start = new DateTime(today.Year, today.Month, 1).ToString("yyyy-MM-dd");
        var end = today.ToString("yyyy-MM-dd");
        using var usageRequest = Request($"{source.BaseUrl.TrimEnd('/')}/dashboard/billing/usage?start_date={start}&end_date={end}", apiKey);
        using var usageResponse = await client.SendAsync(usageRequest, cancellationToken);
        if (!usageResponse.IsSuccessStatusCode) return Failure(usageResponse.StatusCode);

        using var usage = JsonDocument.Parse(await usageResponse.Content.ReadAsStringAsync(cancellationToken));
        var used = ProviderJson.Number(usage.RootElement, "total_usage") / 100d;
        var observedAt = DateTimeOffset.UtcNow;
        var snapshot = new QuotaSnapshot
        {
            SourceId = source.Id,
            SourceName = source.Name,
            WindowKey = "balance",
            Label = "Balance",
            Used = used,
            Limit = limit > 0 ? limit : null,
            Remaining = limit > 0 ? Math.Max(0, limit - used) : null,
            Unit = "USD",
            ResetAt = null,
            ObservedAt = observedAt,
            Detail = accessUntil?.ToString("O")
        };
        return new QuotaPollResult { Snapshots = [snapshot], Status = ProviderStatus.Healthy };
    }

    private static HttpRequestMessage Request(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static QuotaPollResult Failure(HttpStatusCode statusCode) => new()
    {
        Status = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ProviderStatus.AuthenticationFailed
            : ProviderStatus.Unavailable,
        Error = $"HTTP {(int)statusCode}"
    };
}
