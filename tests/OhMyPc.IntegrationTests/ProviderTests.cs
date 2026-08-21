using System.Net;
using System.Text;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Providers;

namespace OhMyPc.IntegrationTests;

public sealed class ProviderTests
{
    [Fact]
    public async Task Sub2Api_ParsesSubscriptionWindows()
    {
        const string json = """
            {
              "subscription": {
                "daily_limit_usd": 10,
                "daily_usage_usd": 2.5,
                "weekly_limit_usd": 50,
                "weekly_usage_usd": 17,
                "monthly_limit_usd": 150,
                "monthly_usage_usd": 45,
                "weekly_window_start": "2026-08-03T00:00:00Z",
                "expires_at": "2026-09-01T00:00:00Z"
              }
            }
            """;
        var handler = new QueueHandler(Response(json));
        var provider = new Sub2ApiProvider(new StubHttpClientFactory(handler));

        var result = await provider.PollAsync(Source(DataSourceKind.Sub2Api), "token");

        Assert.Equal(ProviderStatus.Healthy, result.Status);
        Assert.Equal(3, result.Snapshots.Count);
        var daily = result.Snapshots.Single(x => x.WindowKey == "daily");
        Assert.Equal(7.5, daily.Remaining);
        Assert.Equal(TimeSpan.Zero, daily.ResetAt!.Value.TimeOfDay);
        Assert.Equal(33, result.Snapshots.Single(x => x.WindowKey == "weekly").Remaining);
        Assert.Equal("Bearer token", handler.Authorizations.Single());
        Assert.EndsWith("/usage?days=90", handler.Urls.Single());
    }

    [Fact]
    public async Task NewApi_ParsesBillingLimitAndCentBasedUsage()
    {
        var handler = new QueueHandler(
            Response("""{"hard_limit_usd":100,"access_until":"2026-09-01T00:00:00Z"}"""),
            Response("""{"total_usage":1234}"""));
        var provider = new NewApiProvider(new StubHttpClientFactory(handler));

        var result = await provider.PollAsync(Source(DataSourceKind.NewApi), "token");
        var snapshot = Assert.Single(result.Snapshots);

        Assert.Equal(12.34, snapshot.Used, 2);
        Assert.Equal(87.66, snapshot.Remaining!.Value, 2);
        Assert.Equal("balance", snapshot.WindowKey);
        Assert.Null(snapshot.ResetAt);
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer token", value));
        Assert.Contains(handler.Urls, url => url.EndsWith("/dashboard/billing/subscription"));
        Assert.Contains(handler.Urls, url => url.Contains("/dashboard/billing/usage?start_date="));
    }

    [Fact]
    public void NewApiBalance_TracksTheLatestRechargeAsProgressLimit()
    {
        var initial = Balance(800, 1000, 200);
        QuotaRefreshService.ApplyNewApiBalanceProgressLimit(initial, previous: null);
        var spent = Balance(820, 1000, 180);
        QuotaRefreshService.ApplyNewApiBalanceProgressLimit(spent, initial);
        var recharged = Balance(830, 1200, 370);
        QuotaRefreshService.ApplyNewApiBalanceProgressLimit(recharged, spent);
        var spentAgain = Balance(850, 1200, 350);
        QuotaRefreshService.ApplyNewApiBalanceProgressLimit(spentAgain, recharged);

        Assert.Equal(200, initial.ProgressLimit);
        Assert.Equal(200, spent.ProgressLimit);
        Assert.Equal(90, spent.RemainingPercent);
        Assert.Equal(370, recharged.ProgressLimit);
        Assert.Equal(100, recharged.RemainingPercent);
        Assert.Equal(370, spentAgain.ProgressLimit);
        Assert.Equal(350d / 370d * 100d, spentAgain.RemainingPercent);
    }

    [Fact]
    public async Task ZhipuCodingPlan_ParsesTokenAndMcpLimits()
    {
        var handler = new QueueHandler(
            Response("{\"data\":{}}"),
            Response("{\"data\":{}}"),
            Response("""
                {
                  "data": {
                    "limits": [
                      { "type": "TOKENS_LIMIT", "percentage": 3, "nextResetTime": 1786260228545 },
                      { "type": "TIME_LIMIT", "usage": 1000, "currentValue": 344, "remaining": 656, "nextResetTime": 1786868866997 }
                    ]
                  }
                }
        """));
        var provider = new ZhipuCodingPlanProvider(new StubHttpClientFactory(handler));
        var source = Source(DataSourceKind.ZhipuCodingPlan);
        source.BaseUrl = "https://api.z.ai/api/anthropic";

        var result = await provider.PollAsync(source, "token");

        Assert.Equal(ProviderStatus.Healthy, result.Status);
        Assert.Equal(2, result.Snapshots.Count);
        var tokens = result.Snapshots.Single(x => x.WindowKey == "zhipu-token-5h");
        Assert.Equal(3, tokens.Used);
        Assert.Equal(97, tokens.Remaining);
        Assert.Equal("%", tokens.Unit);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1786260228545), tokens.ResetAt);
        var mcp = result.Snapshots.Single(x => x.WindowKey == "zhipu-mcp-monthly");
        Assert.Equal(344, mcp.Used);
        Assert.Equal(1000, mcp.Limit);
        Assert.Equal(656, mcp.Remaining);
        Assert.Equal("calls", mcp.Unit);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1786868866997), mcp.ResetAt);
        Assert.All(handler.Authorizations, value => Assert.Equal("token", value));
        Assert.Contains(handler.Urls, url => url.Contains("/api/monitor/usage/model-usage?startTime=") && url.Contains("&endTime="));
        Assert.Contains(handler.Urls, url => url.Contains("/api/monitor/usage/tool-usage?startTime=") && url.Contains("&endTime="));
        Assert.Contains(handler.Urls, url => url.EndsWith("/api/monitor/usage/quota/limit"));
    }

    [Fact]
    public async Task ZhipuCodingPlan_MapsUnauthorizedResponseToAuthenticationFailure()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = new ZhipuCodingPlanProvider(new StubHttpClientFactory(handler));

        var result = await provider.PollAsync(Source(DataSourceKind.ZhipuCodingPlan), "bad-token");

        Assert.Equal(ProviderStatus.AuthenticationFailed, result.Status);
        Assert.Equal("HTTP 401", result.Error);
    }

    [Fact]
    public async Task Provider_MapsUnauthorizedResponseToAuthenticationFailure()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = new Sub2ApiProvider(new StubHttpClientFactory(handler));

        var result = await provider.PollAsync(Source(DataSourceKind.Sub2Api), "bad-token");

        Assert.Equal(ProviderStatus.AuthenticationFailed, result.Status);
        Assert.Equal("HTTP 401", result.Error);
    }

    private static DataSourceDefinition Source(DataSourceKind kind) => new()
    {
        Id = "source",
        Name = "Source",
        Kind = kind,
        BaseUrl = "https://example.test/v1"
    };

    private static QuotaSnapshot Balance(double used, double limit, double remaining) => new()
    {
        WindowKey = "balance",
        Used = used,
        Limit = limit,
        Remaining = remaining
    };

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: true);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string> Urls { get; } = [];
        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Authorizations.Add(request.Headers.Authorization!.ToString());
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
