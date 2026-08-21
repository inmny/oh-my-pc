using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Providers;

public sealed class ZhipuCodingPlanProvider(IHttpClientFactory clientFactory) : IQuotaProvider
{
    public const string DefaultBaseUrl = "https://api.z.ai/api/anthropic";

    public DataSourceKind Kind => DataSourceKind.ZhipuCodingPlan;

    public async Task<QuotaPollResult> PollAsync(DataSourceDefinition source, string apiKey, CancellationToken cancellationToken = default)
    {
        var origin = new Uri(source.BaseUrl).GetLeftPart(UriPartial.Authority);
        var query = UsageQuery();
        var client = clientFactory.CreateClient("providers");

        try
        {
            using var modelUsage = await GetAsync(client, $"{origin}/api/monitor/usage/model-usage{query}", apiKey, cancellationToken);
            using var toolUsage = await GetAsync(client, $"{origin}/api/monitor/usage/tool-usage{query}", apiKey, cancellationToken);
            using var quotaLimit = await GetAsync(client, $"{origin}/api/monitor/usage/quota/limit", apiKey, cancellationToken);
            return ParseQuota(source, quotaLimit);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is not null)
        {
            return Failure(exception.StatusCode.Value);
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string url, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static QuotaPollResult ParseQuota(DataSourceDefinition source, JsonDocument document)
    {
        var data = document.RootElement.TryGetProperty("data", out var value) ? value : document.RootElement;
        var observedAt = DateTimeOffset.UtcNow;
        var snapshots = new List<QuotaSnapshot>();
        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                var type = ProviderJson.Text(limit, "type");
                if (type == "TOKENS_LIMIT")
                {
                    var usedPercent = Math.Clamp(ProviderJson.Number(limit, "percentage"), 0, 100);
                    snapshots.Add(new QuotaSnapshot
                    {
                        SourceId = source.Id,
                        SourceName = source.Name,
                        WindowKey = "zhipu-token-5h",
                        Label = "Token usage (5 Hour)",
                        Used = usedPercent,
                        Limit = 100,
                        Remaining = 100 - usedPercent,
                        Unit = "%",
                        ResetAt = UnixMilliseconds(limit, "nextResetTime"),
                        ObservedAt = observedAt
                    });
                }
                else if (type == "TIME_LIMIT")
                {
                    var usage = ProviderJson.Number(limit, "usage");
                    var used = ProviderJson.Number(limit, "currentValue");
                    if (usage <= 0) continue;
                    snapshots.Add(new QuotaSnapshot
                    {
                        SourceId = source.Id,
                        SourceName = source.Name,
                        WindowKey = "zhipu-mcp-monthly",
                        Label = "MCP usage (1 Month)",
                        Used = used,
                        Limit = usage,
                        Remaining = ProviderJson.Number(limit, "remaining", Math.Max(0, usage - used)),
                        Unit = "calls",
                        ResetAt = UnixMilliseconds(limit, "nextResetTime"),
                        ObservedAt = observedAt
                    });
                }
            }
        }

        return new QuotaPollResult { Snapshots = snapshots, Status = ProviderStatus.Healthy };
    }

    private static DateTimeOffset? UnixMilliseconds(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return null;
        if (item.TryGetInt64(out var milliseconds) && milliseconds > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        return null;
    }

    private static string UsageQuery()
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddDays(-1);
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 59, 59, now.Offset);
        var startText = Uri.EscapeDataString(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var endText = Uri.EscapeDataString(end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        return $"?startTime={startText}&endTime={endText}";
    }

    private static QuotaPollResult Failure(HttpStatusCode statusCode) => new()
    {
        Status = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ProviderStatus.AuthenticationFailed
            : ProviderStatus.Unavailable,
        Error = $"HTTP {(int)statusCode}"
    };
}
