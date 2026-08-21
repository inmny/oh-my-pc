using System.Net;
using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Vpn;

public sealed class PassGoClient(IHttpClientFactory httpClientFactory) : IVpnQuotaClient
{
    public async Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(email), "email" },
            { new StringContent(password), "password" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "passport/auth/login") { Content = content };
        request.Headers.TryAddWithoutValidation("theme-ua", "mala-pro");
        using var response = await httpClientFactory.CreateClient("passgo").SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("auth_data", out var authData))
        {
            throw new PassGoApiException(GetMessage(root, "登录失败"), false);
        }
        return authData.GetString()!;
    }

    public async Task<VpnSubscriptionSnapshot> GetSubscriptionAsync(
        string authData,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "user/getSubscribe");
        request.Headers.TryAddWithoutValidation("Authorization", authData);
        request.Headers.TryAddWithoutValidation("theme-ua", "mala-pro");
        using var response = await httpClientFactory.CreateClient("passgo").SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new PassGoApiException(GetMessage(root, "无法读取订阅信息"), false);
        }

        var expiresAt = data.TryGetProperty("expired_at", out var expiresAtElement)
            && expiresAtElement.ValueKind == JsonValueKind.Number
            ? expiresAtElement.GetInt64()
            : 0;
        var resetDay = data.TryGetProperty("reset_day", out var resetDayElement)
            && resetDayElement.ValueKind == JsonValueKind.Number
            ? resetDayElement.GetInt32()
            : (int?)null;
        var planName = data.TryGetProperty("plan", out var plan)
            && plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("name", out var name)
            ? name.GetString() ?? ""
            : "";

        return new VpnSubscriptionSnapshot
        {
            Email = data.GetProperty("email").GetString()!,
            PlanName = planName,
            UploadedBytes = data.GetProperty("u").GetInt64(),
            DownloadedBytes = data.GetProperty("d").GetInt64(),
            TransferLimitBytes = data.GetProperty("transfer_enable").GetInt64(),
            ExpiresAt = expiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(expiresAt) : null,
            ResetDay = resetDay > 0 ? resetDay : null
        };
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PassGoApiException("登录状态已失效", true);
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = PassGoResponseDecoder.Decode(payload);
        if (!response.IsSuccessStatusCode)
        {
            var message = GetMessage(document.RootElement, $"HTTP {(int)response.StatusCode}");
            document.Dispose();
            throw new PassGoApiException(message, false);
        }
        return document;
    }

    private static string GetMessage(JsonElement root, string fallback) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? fallback
            : fallback;
}

public sealed class PassGoApiException(string message, bool authenticationFailed) : Exception(message)
{
    public bool AuthenticationFailed { get; } = authenticationFailed;
}
