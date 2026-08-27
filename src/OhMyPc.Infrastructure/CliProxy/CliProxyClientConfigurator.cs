using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>
/// 把 CLIProxyAPI 的模型定义写入 zcode / opencode / dsh 的配置文件。
/// 全部采用结构化编辑：只改本应用管理的键，保留各客户端配置中的其余字段（zcode.* 元数据、费用配置等）。
/// </summary>
public sealed class CliProxyClientConfigurator(
    string? zcodeDesktopConfig = null,
    string? zcodeCliConfig = null,
    string? opencodeConfig = null,
    string? dshSettings = null,
    string? dshCredentials = null) : IClientConfigurator
{
    public const string DefaultProviderId = "cli-proxy-api";
    public const string DefaultDshEnvName = "CLIPROXYAPI_API_KEY";
    private const string DisplayName = "CLIProxyAPI";

    private readonly string _zcodeDesktopConfig = zcodeDesktopConfig ?? ProxyClientPaths.ZcodeDesktopConfig;
    private readonly string _zcodeCliConfig = zcodeCliConfig ?? ProxyClientPaths.ZcodeCliConfig;
    private readonly string _opencodeConfig = opencodeConfig ?? ProxyClientPaths.OpencodeConfig;
    private readonly string _dshSettings = dshSettings ?? ProxyClientPaths.DshSettings;
    private readonly string _dshCredentials = dshCredentials ?? ProxyClientPaths.DshCredentials;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Task<ClientSyncResult> SyncAsync(ClientSyncPlan plan, CancellationToken cancellationToken = default) => plan.Client switch
    {
        ProxyClientKind.Zcode => SyncZcodeAsync(plan, cancellationToken),
        ProxyClientKind.Opencode => SyncOpencodeAsync(plan, cancellationToken),
        ProxyClientKind.Dsh => SyncDshAsync(plan, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Client, "未知的客户端类型")
    };

    private static bool DirectoryExistsFor(string path) =>
        Directory.Exists(Path.GetDirectoryName(path));

    private async Task<ClientSyncResult> SyncZcodeAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        if (!DirectoryExistsFor(_zcodeDesktopConfig) && !DirectoryExistsFor(_zcodeCliConfig))
            throw new InvalidOperationException("未找到 zcode 配置目录（~/.zcode）。");

        // zcode 只支持 anthropic 与 openai(responses) 两种 API 格式：按上游协议类型拆成两个 provider 条目。
        var anthropicModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Claude).Select(model => model.Config).ToList();
        var openaiModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Codex).Select(model => model.Config).ToList();

        string? anthropicId = null;
        string? openaiId = null;
        var written = new List<string>();
        foreach (var (path, isCliConfig) in new[]
                 {
                     (_zcodeDesktopConfig, false),
                     (_zcodeCliConfig, true)
                 })
        {
            var root = await ReadJsonObjectAsync(path, cancellationToken);
            var providers = GetOrCreateObject(root, "provider");
            anthropicId ??= DetectProviderId(providers, plan.BaseUrl, "anthropic") ?? plan.ProviderId;
            openaiId ??= DetectProviderId(providers, plan.BaseUrl, "openai") ?? $"{plan.ProviderId}-codex";

            if (anthropicModels.Count > 0)
            {
                var provider = UpsertZcodeProvider(providers, anthropicId, "anthropic", "CLIProxyAPI", plan, isCliConfig);
                SyncZcodeModels(provider, anthropicModels);
            }
            if (openaiModels.Count > 0)
            {
                var provider = UpsertZcodeProvider(providers, openaiId, "openai", "CLIProxyAPI Codex", plan, isCliConfig);
                SyncZcodeModels(provider, openaiModels);
            }

            if (plan.DefaultModelId is not null)
            {
                var defaultIsCodex = plan.Models.Any(model =>
                    model.Kind == ProxyProviderKind.Codex
                    && string.Equals(model.Config.GetId(), plan.DefaultModelId, StringComparison.OrdinalIgnoreCase));
                root["model"] = $"{(defaultIsCodex ? openaiId : anthropicId)}/{plan.DefaultModelId}";
            }
            await WriteAsync(path, root, cancellationToken);
            written.Add(path);
        }
        return BuildResult(anthropicId ?? plan.ProviderId, written, plan);
    }

    private static JsonObject UpsertZcodeProvider(
        JsonObject providers, string providerId, string kind, string displayName, ClientSyncPlan plan, bool isCliConfig)
    {
        var provider = GetOrCreateObject(providers, providerId);
        provider["name"] = displayName;
        provider["kind"] = kind;
        provider["source"] = "custom";
        provider["enabled"] = true;
        if (isCliConfig)
        {
            provider["apiFormat"] = kind == "openai" ? "openai-responses" : "anthropic-messages";
            provider["defaultKind"] = kind;
            provider["npm"] = kind == "openai" ? "@ai-sdk/openai" : "@ai-sdk/anthropic";
        }
        var options = GetOrCreateObject(provider, "options");
        options["apiKey"] = plan.ApiKey;
        options["baseURL"] = plan.BaseUrl;
        return provider;
    }

    /// <summary>模型列表以本次同步为准：upsert 计划内模型，移除不在计划内的旧条目（修复早期按单一协议同步留下的混排状态）。</summary>
    private static void SyncZcodeModels(JsonObject provider, IReadOnlyList<ProxyModelConfig> models)
    {
        var target = GetOrCreateObject(provider, "models");
        foreach (var model in models)
        {
            UpsertModel(target, model.GetId(), ProxyMappers.ToZcodeModel(model));
        }
        var keep = models.Select(model => model.GetId()).ToHashSet(StringComparer.Ordinal);
        foreach (var id in target.Select(pair => pair.Key).ToList())
        {
            if (!keep.Contains(id)) target.Remove(id);
        }
    }

    private async Task<ClientSyncResult> SyncOpencodeAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        var path = _opencodeConfig;
        if (!DirectoryExistsFor(path))
            throw new InvalidOperationException("未找到 opencode 配置目录（~/.config/opencode）。");

        var root = await ReadJsonObjectAsync(path, cancellationToken);
        var providers = GetOrCreateObject(root, "provider");
        var providerId = DetectProviderId(providers, $"{plan.BaseUrl}/v1", kind: null) ?? plan.ProviderId;
        var provider = GetOrCreateObject(providers, providerId);
        provider["name"] = DisplayName;
        provider["npm"] = "@ai-sdk/openai-compatible";
        var options = GetOrCreateObject(provider, "options");
        options["apiKey"] = plan.ApiKey;
        options["baseURL"] = $"{plan.BaseUrl}/v1";
        var models = GetOrCreateObject(provider, "models");
        var keep = plan.Models.Select(model => model.Config.GetId()).ToHashSet(StringComparer.Ordinal);
        foreach (var model in plan.Models)
        {
            UpsertModel(models, model.Config.GetId(), ProxyMappers.ToOpencodeModel(model.Config));
        }
        // 权威模式：移除不在本次同步内的旧条目（如 EasyCPA 时代已不可路由的小写 id）
        foreach (var id in models.Select(pair => pair.Key).ToList())
        {
            if (!keep.Contains(id)) models.Remove(id);
        }
        if (plan.DefaultModelId is not null) root["model"] = $"{providerId}/{plan.DefaultModelId}";
        await WriteAsync(path, root, cancellationToken);
        return BuildResult(providerId, [path], plan);
    }

    private async Task<ClientSyncResult> SyncDshAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        if (!DirectoryExistsFor(_dshSettings))
            throw new InvalidOperationException("未找到 dsh 配置目录（~/.dsh）。");

        // dsh 支持 anthropic-messages / openai-completions / openai-responses：
        // Claude 上游走 anthropic-messages（原生协议，baseURL 为网关根地址），
        // Codex 上游走 openai-responses（baseURL 带 /v1）。
        var anthropicModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Claude).Select(model => model.Config).ToList();
        var responsesModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Codex).Select(model => model.Config).ToList();

        var root = await YamlTree.ReadRootAsync(_dshSettings, cancellationToken);
        var providers = YamlTree.GetOrCreateMapping(YamlTree.GetOrCreateMapping(root, "llm-pi-ai"), "providers");
        var gatewayBase = plan.BaseUrl.TrimEnd('/');
        var existing = DshGatewayProviders(providers, gatewayBase).ToList();
        // anthropic 组优先复用既有 anthropic-messages 条目；否则升级复用旧 openai-completions 条目的 id
        var anthropicId = existing.FirstOrDefault(entry => entry.Api == "anthropic-messages").Id
            ?? existing.FirstOrDefault(entry => entry.Api == "openai-completions").Id
            ?? plan.ProviderId;
        var responsesId = existing.FirstOrDefault(entry => entry.Api == "openai-responses").Id ?? $"{plan.ProviderId}-responses";
        var envName = existing.FirstOrDefault(entry => entry.EnvName is not null).EnvName ?? DefaultDshEnvName;

        WriteDshGroup(providers, anthropicId, "anthropic-messages", "CLIProxyAPI", envName, gatewayBase, anthropicModels, removeWhenEmpty: existing.Any(entry => entry.Id == anthropicId));
        WriteDshGroup(providers, responsesId, "openai-responses", "CLIProxyAPI Responses", envName, $"{gatewayBase}/v1", responsesModels, removeWhenEmpty: existing.Any(entry => entry.Id == responsesId));

        if (plan.DefaultModelId is not null)
        {
            var defaultIsCodex = plan.Models.Any(model =>
                model.Kind == ProxyProviderKind.Codex
                && string.Equals(model.Config.GetId(), plan.DefaultModelId, StringComparison.OrdinalIgnoreCase));
            var agentDefault = YamlTree.GetOrCreateMapping(root, "agent-default-model");
            YamlTree.SetScalar(agentDefault, "provider", defaultIsCodex ? responsesId : anthropicId);
            YamlTree.SetScalar(agentDefault, "model", plan.DefaultModelId);
            if (plan.DefaultEffort is not null) YamlTree.SetScalar(agentDefault, "reasoningEffort", plan.DefaultEffort);
        }
        ConfigFileSafety.WriteAllText(_dshSettings, YamlTree.Save(root));

        var credentials = await YamlTree.ReadRootAsync(_dshCredentials, cancellationToken);
        var refs = YamlTree.GetOrCreateMapping(credentials, "refs");
        refs.Children[YamlTree.Key(envName)] = YamlTree.Text(plan.ApiKey);
        ConfigFileSafety.WriteAllText(_dshCredentials, YamlTree.Save(credentials));
        return BuildResult(anthropicId, [_dshSettings, _dshCredentials], plan);
    }

    /// <summary>完全覆盖写入一个协议组（同 id 模型按 provider 顺序取第一个）；组内没有模型时移除既有网关条目。</summary>
    private static void WriteDshGroup(
        YamlMappingNode providers, string id, string api, string displayName, string envName,
        string baseUrl, IReadOnlyList<ProxyModelConfig> models, bool removeWhenEmpty)
    {
        if (models.Count == 0)
        {
            if (removeWhenEmpty) YamlTree.Remove(providers, id);
            return;
        }
        var modelsById = new Dictionary<string, ProxyModelConfig>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            modelsById.TryAdd(model.GetId(), model);
        }
        providers.Children[YamlTree.Key(id)] = new YamlMappingNode
        {
            Children =
            {
                [YamlTree.Key("displayName")] = YamlTree.Text(displayName),
                [YamlTree.Key("apiKeyEnv")] = YamlTree.Text(envName),
                [YamlTree.Key("api")] = YamlTree.Plain(api),
                [YamlTree.Key("baseURL")] = YamlTree.Text(baseUrl),
                [YamlTree.Key("models")] = new YamlSequenceNode(modelsById.Values
                    .Select(model => YamlTree.ToNode(ProxyMappers.ToDshModel(model))))
            }
        };
    }

    /// <summary>找出已指向本网关的 dsh provider 条目（baseURL 为根地址或带 /v1 均算），用于复用既有 id。</summary>
    private static IEnumerable<(string Id, string? Api, string? EnvName)> DshGatewayProviders(YamlMappingNode providers, string gatewayBase)
    {
        foreach (var (key, value) in providers.Children)
        {
            if (value is not YamlMappingNode candidate) continue;
            var existing = YamlTree.Scalar(candidate, "baseURL")?.TrimEnd('/');
            if (existing != gatewayBase && existing != $"{gatewayBase}/v1") continue;
            if ((key as YamlScalarNode)?.Value is not { } id) continue;
            yield return (id, YamlTree.Scalar(candidate, "api"), YamlTree.Scalar(candidate, "apiKeyEnv"));
        }
    }

    /// <summary>查找 baseURL 已指向 CLIProxyAPI 的既有 provider，复用其 id（如旧 cpa-gui 条目）；kind 非空时还要求协议一致。</summary>
    private static string? DetectProviderId(JsonObject providers, string baseUrl, string? kind)
    {
        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject entry) continue;
            if (kind is not null && (string?)entry["kind"] != kind) continue;
            var existing = (string?)entry["options"]?["baseURL"];
            if (existing is not null && existing.TrimEnd('/').StartsWith(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }
        return null;
    }

    private static ClientSyncResult BuildResult(string providerId, IReadOnlyList<string> written, ClientSyncPlan plan) => new()
    {
        WrittenFiles = written,
        ProviderId = providerId,
        DefaultModel = plan.DefaultModelId is null ? null : $"{providerId}/{plan.DefaultModelId}"
    };

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new JsonObject();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return node switch
        {
            JsonObject obj => obj,
            null => new JsonObject(),
            _ => throw new InvalidOperationException($"客户端配置文件格式异常：{path}")
        };
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name) =>
        parent[name] as JsonObject ?? (JsonObject)(parent[name] = new JsonObject());

    /// <summary>upsert 模型字段；cost 逐键合并以保留客户端已有的未知费用子键（如 dsh 的 tiers）。</summary>
    private static void UpsertModel(JsonObject parent, string name, Dictionary<string, object?> fields)
    {
        var target = GetOrCreateObject(parent, name);
        if (fields.GetValueOrDefault("cost") is Dictionary<string, object?> costFields)
        {
            var cost = GetOrCreateObject(target, "cost");
            foreach (var (key, value) in costFields)
            {
                cost[key] = ToNode(value);
            }
        }
        foreach (var (key, value) in fields)
        {
            if (key == "cost") continue;
            target[key] = ToNode(value);
        }
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        decimal m => JsonValue.Create(m),
        IEnumerable<string> list => new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        Dictionary<string, object?> map => new JsonObject(map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ToNode(pair.Value)))),
        _ => throw new NotSupportedException($"不支持的 JSON 值类型：{value.GetType().Name}")
    };

    private static async Task WriteAsync(string path, JsonObject root, CancellationToken cancellationToken)
    {
        // ToJsonString 无异步版本，文件很小，直接写。
        await Task.Run(() => ConfigFileSafety.WriteAllText(path, root.ToJsonString(JsonOptions)), cancellationToken);
    }
}
