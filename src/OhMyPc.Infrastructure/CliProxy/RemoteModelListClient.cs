using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>按 provider 协议从中转站拉取模型 id 列表：Codex 走 Bearer，Claude 走 x-api-key。</summary>
public sealed class RemoteModelListClient(IHttpClientFactory httpClientFactory) : IRemoteModelListClient
{
    public async Task<IReadOnlyList<string>> FetchModelIdsAsync(ProxyProviderConfig provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl) || string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("请先填写该 Provider 的上游地址与 API 密钥。");

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/models" : $"{baseUrl}/v1/models";
        var client = httpClientFactory.CreateClient("providers");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (provider.Kind == ProxyProviderKind.Claude)
        {
            request.Headers.Add("x-api-key", provider.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return payload?["data"]?.AsArray()
            .Select(entry => (string?)entry?["id"])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }
}
