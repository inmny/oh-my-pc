using Microsoft.Extensions.Logging;
using System.Text.Json;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Providers;

public sealed class EnvironmentSourceImporter(
    IAppStore store,
    IEnumerable<IQuotaProvider> providers,
    ILogger<EnvironmentSourceImporter> logger)
{
    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var existing = await store.ListDataSourcesAsync(cancellationToken);
        var imported = 0;
        foreach (var slot in new[] { "PRIMARY", "SECONDARY", "BACKUP" })
        {
            var baseUrl = Environment.GetEnvironmentVariable($"AI_INPUT_{slot}_BASE_URL");
            var apiKey = Environment.GetEnvironmentVariable($"AI_INPUT_{slot}_API_KEY");
            var modelStatusUrl = Environment.GetEnvironmentVariable($"AI_INPUT_{slot}_STATUS_URL");
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) continue;

            var id = $"ai-input-{slot.ToLowerInvariant()}";
            if (existing.Any(x => x.Id == id)) continue;
            var source = new DataSourceDefinition
            {
                Id = id,
                Name = $"AI Input {char.ToUpperInvariant(slot[0])}{slot[1..].ToLowerInvariant()}",
                BaseUrl = baseUrl.TrimEnd('/'),
                ModelStatusUrl = modelStatusUrl?.Trim() ?? "",
                Kind = await DetectKindAsync(baseUrl, apiKey, slot, cancellationToken),
                PollIntervalSeconds = 300,
                Enabled = true
            };
            await store.SaveDataSourceAsync(source, apiKey, cancellationToken);
            imported++;
        }

        imported += await ImportOpenCodeAuthAsync(existing, cancellationToken);

        if (imported > 0) logger.LogInformation("Imported {Count} external quota sources", imported);
        return imported;
    }

    private async Task<int> ImportOpenCodeAuthAsync(
        IReadOnlyList<DataSourceDefinition> existing,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode",
            "auth.json");
        if (!File.Exists(path)) return 0;

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("zhipuai-coding-plan", out var credentials)
            || !credentials.TryGetProperty("key", out var keyValue)
            || keyValue.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        var apiKey = keyValue.GetString();
        if (string.IsNullOrWhiteSpace(apiKey)) return 0;

        var source = existing.SingleOrDefault(x => x.Id == "zhipuai-coding-plan");
        var isNew = source is null;
        source ??= new DataSourceDefinition
        {
            Id = "zhipuai-coding-plan",
            Name = "智谱 Coding Plan",
            BaseUrl = ZhipuCodingPlanProvider.DefaultBaseUrl,
            PollIntervalSeconds = 300,
            Enabled = true
        };
        source.Kind = DataSourceKind.ZhipuCodingPlan;
        if (string.IsNullOrWhiteSpace(source.BaseUrl)) source.BaseUrl = ZhipuCodingPlanProvider.DefaultBaseUrl;
        await store.SaveDataSourceAsync(source, apiKey.Trim(), cancellationToken);
        return isNew ? 1 : 0;
    }

    private async Task<DataSourceKind> DetectKindAsync(string baseUrl, string apiKey, string slot, CancellationToken cancellationToken)
    {
        var probe = new DataSourceDefinition { Id = "probe", Name = "Probe", BaseUrl = baseUrl };
        var sub2Api = providers.Single(x => x.Kind == DataSourceKind.Sub2Api);
        try
        {
            var result = await sub2Api.PollAsync(probe, apiKey, cancellationToken);
            if (result.Status == ProviderStatus.Healthy) return DataSourceKind.Sub2Api;
        }
        catch (HttpRequestException)
        {
        }
        return slot == "BACKUP" ? DataSourceKind.NewApi : DataSourceKind.Sub2Api;
    }
}
