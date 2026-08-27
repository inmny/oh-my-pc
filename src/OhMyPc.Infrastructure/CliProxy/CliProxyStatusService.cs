using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>用 /v1/models 探测 CLIProxyAPI 运行状态与暴露的模型数量。</summary>
public sealed class CliProxyStatusService(
    IHttpClientFactory httpClientFactory,
    IProxyConfigStore store,
    ICliProxyInstaller installer,
    ILogger<CliProxyStatusService> logger) : IProxyStatusService
{
    private ProxyServiceStatus _last = new();

    public event EventHandler? Refreshed;

    public ProxyServiceStatus Last => _last;

    public async Task<ProxyServiceStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await ProbeAsync(cancellationToken);
        _last = status;
        Refreshed?.Invoke(this, EventArgs.Empty);
        return status;
    }

    private async Task<ProxyServiceStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!installer.IsInstalled())
        {
            return new ProxyServiceStatus();
        }

        ProxyConfigSnapshot snapshot;
        try
        {
            snapshot = await store.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException)
        {
            return new ProxyServiceStatus { Version = installer.GetInstalledVersion() };
        }

        var baseUrl = snapshot.Access.GetBaseUrl();
        try
        {
            var client = httpClientFactory.CreateClient("proxy-status");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            if (snapshot.Access.ApiKeys.FirstOrDefault() is { } apiKey)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var modelCount = payload?["data"]?.AsArray().Count ?? 0;
            return new ProxyServiceStatus
            {
                State = ProxyProcessState.Running,
                BaseUrl = baseUrl,
                Version = installer.GetInstalledVersion(),
                ModelCount = modelCount
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "CLIProxyAPI 状态探测失败：{BaseUrl}", baseUrl);
            return new ProxyServiceStatus { BaseUrl = baseUrl, Version = installer.GetInstalledVersion() };
        }
    }
}
