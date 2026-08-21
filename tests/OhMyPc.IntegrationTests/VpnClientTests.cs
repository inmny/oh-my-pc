using System.Net;
using System.Text;
using OhMyPc.Infrastructure.Vpn;

namespace OhMyPc.IntegrationTests;

public sealed class VpnClientTests
{
    private const string Source = "nsz{gAWrkXlx08J6Eq:V4[deO1DQTCwm2oB3ty9jSYI]7RM5bHiUaf,c}KuPGpNhZLvF";
    private const string Target = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,[]{}:";

    [Fact]
    public void Decoder_DecodesPublicEndpointResponse()
    {
        const string payload = "bCIsNW81ZCwiTiIsZFpabiwsIjEiWG4sLG84biJOIlxkd3taa1xkezl0Wlxkd3pNSFxkdHpxOSIxImtvNW8iTmwiNVQsX2Q3MCJOSmQwMDEiRixfblhvRjBfeW43RjllIk5NMSJGLF9GSnlGNW5fOVQ3Wm4iTkgxIm5Yb0YwX2lTRjVuMEYsNV8sZDk5RmEiTk8iOFhvRjAuWlRYIjEiUFAuWlRYIjEiTXddLlpUWCIxImVvU1RULlpUWCIxIixGSm8uWlRYIjEiTXp3LlpUWCIxIlRkNTBUVG0uWlRYIjEiZW5vUy5KbjUiMSI5VGFYb0YwLlpUWCJ2MSJGLF9ab0U1WlNvIk5IMSJab0U1WlNvXzVlRW4iTiI3blpvRTVaU28iMSI3blpvRTVaU29fLEY1bl9tbmUiTkpkMDAxIjduWm9FNVpTb195XV8sRjVuX21uZSJOSmQwMDEiN25ab0U1WlNvX3ldXyxaVDduXzVTN24sU1QwayJOSC50MSI1ZDdKLDVGMG5fLEY1bl9tbmUiTkpkMDAxIm9FRV9rbixaN0ZFNUZUSiJOIlxke25ISFxkcXR6blxkdHpdSFxkMzkyblxkdE13M1xkYntIXVxkdHtIe1xkdGJdSCIxIm9FRV9kNzAiTiJTNTVFLE5cL1wvRW8sLDhUei5dd013d3d3LmFlVVwvIjEiMFQ4VCJOSmQwMDEiRixfN25ab0U1WlNvIk5ITDEibjc3VDciTkpkMDBM";

        using var document = PassGoResponseDecoder.Decode(payload);

        Assert.Equal(1, document.RootElement.GetProperty("data").GetProperty("is_email_verify").GetInt32());
    }

    [Fact]
    public async Task Client_LogsInAndParsesSubscription()
    {
        var handler = new CaptureHandler(
            Response(Encode("""{"data":{"auth_data":"raw auth token"}}""")),
            Response(Encode("""{"data":{"email":"user@example.com","u":1073741824,"d":2147483648,"transfer_enable":10737418240,"expired_at":1786406400,"reset_day":15,"plan":{"name":"\u5e74\u5ea6\u5957\u9910"}}}""")));
        var client = new PassGoClient(new StubHttpClientFactory(handler));

        var authData = await client.LoginAsync("user@example.com", "secret-password");
        var quota = await client.GetSubscriptionAsync(authData);

        Assert.Equal("raw auth token", authData);
        Assert.Equal("user@example.com", quota.Email);
        Assert.Equal("年度套餐", quota.PlanName);
        Assert.Equal(1073741824, quota.UploadedBytes);
        Assert.Equal(2147483648, quota.DownloadedBytes);
        Assert.Equal(10737418240, quota.TransferLimitBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786406400), quota.ExpiresAt);
        Assert.Equal(15, quota.ResetDay);
        Assert.All(handler.ThemeUserAgents, value => Assert.Equal("mala-pro", value));
        Assert.Equal("raw auth token", handler.Authorizations[1]);
        Assert.Contains("name=email", handler.Bodies[0]);
        Assert.Contains("user@example.com", handler.Bodies[0]);
        Assert.Contains("name=password", handler.Bodies[0]);
        Assert.Contains("secret-password", handler.Bodies[0]);
    }

    [Fact]
    public async Task Client_MapsForbiddenToAuthenticationFailure()
    {
        var client = new PassGoClient(new StubHttpClientFactory(new CaptureHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden))));

        var exception = await Assert.ThrowsAsync<PassGoApiException>(() => client.GetSubscriptionAsync("expired-token"));

        Assert.True(exception.AuthenticationFailed);
    }

    private static string Encode(string json)
    {
        var value = json;
        for (var round = 0; round < 10; round++)
        {
            value = string.Create(value.Length, value, static (characters, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var mappedIndex = Target.IndexOf(source[index]);
                    characters[index] = mappedIndex >= 0 ? Source[mappedIndex] : source[index];
                }
            });
        }
        return Convert.ToBase64String(Encoding.Latin1.GetBytes(value));
    }

    private static HttpResponseMessage Response(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.ASCII, "text/plain")
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("https://tot.3616666.xyz/api/v1/") };
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CaptureHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string?> Authorizations { get; } = [];
        public List<string?> ThemeUserAgents { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null);
            ThemeUserAgents.Add(request.Headers.GetValues("theme-ua").Single());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
