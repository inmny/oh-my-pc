using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.InputStatus;

public sealed class InputStatusClient(IHttpClientFactory httpClientFactory) : IInputStatusClient
{
    public async Task<IReadOnlyList<InputModelStatus>> GetModelsAsync(
        Uri statusEndpoint,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("input-status");
        using var response = await client.GetAsync(statusEndpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return document.RootElement.GetProperty("services")
            .EnumerateArray()
            .Select(service =>
            {
                var last = service.GetProperty("last");
                return new InputModelStatus
                {
                    Model = service.GetProperty("model").GetString()!,
                    Available = last.GetProperty("ok").GetBoolean(),
                    LatencyMilliseconds = last.TryGetProperty("latency_ms", out var latency) && latency.ValueKind == JsonValueKind.Number
                        ? latency.GetInt64()
                        : null,
                    Error = last.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : null
                };
            })
            .ToArray();
    }
}
