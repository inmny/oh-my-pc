using System.Security.Cryptography;
using System.Text;
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

        return plan.Upstreams.Count > 0
            ? await SyncZcodeDirectAsync(plan, cancellationToken)
            : await SyncZcodeGatewayAsync(plan, cancellationToken);
    }

    private async Task<ClientSyncResult> SyncZcodeGatewayAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
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

            // 两个协议组都无条件 upsert：组内模型为空时清空既有模型，客户端可见模型始终等于本次同步范围。
            var anthropicProvider = UpsertZcodeProvider(providers, anthropicId, "anthropic", "CLIProxyAPI", plan.ApiKey, plan.BaseUrl, isCliConfig);
            SyncZcodeModels(anthropicProvider, anthropicModels);
            var openaiProvider = UpsertZcodeProvider(providers, openaiId, "openai", "CLIProxyAPI Codex", plan.ApiKey, EnsureV1(plan.BaseUrl), isCliConfig);
            SyncZcodeModels(openaiProvider, openaiModels);
            RemoveDanglingDefaultModel(root, RemoveDirectEntries(providers, keep: []));

            await WriteAsync(path, root, cancellationToken);
            written.Add(path);
        }
        return BuildResult(anthropicId ?? plan.ProviderId, written, plan);
    }

    /// <summary>直连模式：每个上游一个 provider 条目，指向其真实地址与密钥，模型用上游真实名。</summary>
    private async Task<ClientSyncResult> SyncZcodeDirectAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        var written = new List<string>();
        foreach (var (path, isCliConfig) in new[]
                 {
                     (_zcodeDesktopConfig, false),
                     (_zcodeCliConfig, true)
                 })
        {
            var root = await ReadJsonObjectAsync(path, cancellationToken);
            var providers = GetOrCreateObject(root, "provider");
            var keep = new List<string>();
            foreach (var upstream in plan.Upstreams)
            {
                var kind = upstream.Kind == ProxyProviderKind.Claude ? "anthropic" : "openai";
                var id = DirectId(upstream.Key);
                keep.Add(id);
                // openai SDK 只拼 /responses，需要带 /v1 的地址；anthropic SDK 自拼 /v1/messages，用上游根地址
                var baseUrl = upstream.Kind == ProxyProviderKind.Claude ? upstream.BaseUrl.TrimEnd('/') : EnsureV1(upstream.BaseUrl);
                var provider = UpsertZcodeProvider(providers, id, kind, upstream.DisplayName, upstream.ApiKey, baseUrl, isCliConfig);
                SyncZcodeModels(provider, [.. upstream.Models.Select(WithoutAlias)]);
            }
            RemoveDanglingDefaultModel(root, RemoveGatewayEntries(providers, plan.BaseUrl));
            RemoveDanglingDefaultModel(root, RemoveDirectEntries(providers, keep));

            await WriteAsync(path, root, cancellationToken);
            written.Add(path);
        }
        return BuildResult("direct", written, plan);
    }

    private static JsonObject UpsertZcodeProvider(
        JsonObject providers, string providerId, string kind, string displayName, string apiKey, string baseUrl, bool isCliConfig)
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
        options["apiKey"] = apiKey;
        // anthropic SDK 在 baseURL 后拼 /v1/messages，用根地址；openai SDK 只拼 /responses，必须自带 /v1
        options["baseURL"] = kind == "openai" ? baseUrl.TrimEnd('/') : baseUrl;
        return provider;
    }

    /// <summary>模型列表以本次同步为准：upsert 计划内模型，移除不在计划内的旧条目；计划为空时移除整个 models 键。</summary>
    private static void SyncZcodeModels(JsonObject provider, IReadOnlyList<ProxyModelConfig> models)
    {
        if (models.Count == 0)
        {
            provider.Remove("models");
            return;
        }
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
        if (plan.Upstreams.Count > 0)
        {
            var keep = new List<string>();
            foreach (var upstream in plan.Upstreams)
            {
                var id = DirectId(upstream.Key);
                keep.Add(id);
                var provider = GetOrCreateObject(providers, id);
                provider["name"] = upstream.DisplayName;
                provider["npm"] = "@ai-sdk/openai-compatible";
                var options = GetOrCreateObject(provider, "options");
                options["apiKey"] = upstream.ApiKey;
                // openai-compatible SDK 拼 /chat/completions，实测中转站 chat 端点都在 /v1 下（Heju 等已带 /v1 的原样保留）
                options["baseURL"] = EnsureV1(upstream.BaseUrl);
                UpsertOpencodeModels(GetOrCreateObject(provider, "models"), [.. upstream.Models.Select(WithoutAlias)]);
            }
            RemoveDanglingDefaultModel(root, RemoveGatewayEntries(providers, plan.BaseUrl));
            RemoveDanglingDefaultModel(root, RemoveDirectEntries(providers, keep));
            await WriteAsync(path, root, cancellationToken);
            return BuildResult("direct", [path], plan);
        }

        var providerId = DetectProviderId(providers, $"{plan.BaseUrl}/v1", kind: null) ?? plan.ProviderId;
        var gatewayProvider = GetOrCreateObject(providers, providerId);
        gatewayProvider["name"] = DisplayName;
        gatewayProvider["npm"] = "@ai-sdk/openai-compatible";
        var gatewayOptions = GetOrCreateObject(gatewayProvider, "options");
        gatewayOptions["apiKey"] = plan.ApiKey;
        gatewayOptions["baseURL"] = $"{plan.BaseUrl}/v1";
        UpsertOpencodeModels(GetOrCreateObject(gatewayProvider, "models"), [.. plan.Models.Select(model => model.Config)]);
        RemoveDanglingDefaultModel(root, RemoveDirectEntries(providers, keep: []));
        await WriteAsync(path, root, cancellationToken);
        return BuildResult(providerId, [path], plan);
    }

    /// <summary>权威覆盖 provider 的模型表：计划为空时移除整个 models 键。</summary>
    private static void UpsertOpencodeModels(JsonObject models, IReadOnlyList<ProxyModelConfig> configs)
    {
        if (configs.Count == 0)
        {
            models.Clear();
            return;
        }
        foreach (var model in configs)
        {
            UpsertModel(models, model.GetId(), ProxyMappers.ToOpencodeModel(model));
        }
        var keep = configs.Select(model => model.GetId()).ToHashSet(StringComparer.Ordinal);
        foreach (var id in models.Select(pair => pair.Key).ToList())
        {
            if (!keep.Contains(id)) models.Remove(id);
        }
    }

    private async Task<ClientSyncResult> SyncDshAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        if (!DirectoryExistsFor(_dshSettings))
            throw new InvalidOperationException("未找到 dsh 配置目录（~/.dsh）。");

        var root = await YamlTree.ReadRootAsync(_dshSettings, cancellationToken);
        var providers = YamlTree.GetOrCreateMapping(YamlTree.GetOrCreateMapping(root, "llm-pi-ai"), "providers");
        var gatewayBase = plan.BaseUrl.TrimEnd('/');
        ClientSyncResult result;
        if (plan.Upstreams.Count > 0)
        {
            result = await WriteDshDirectAsync(plan, root, providers, gatewayBase, cancellationToken);
        }
        else
        {
            result = await WriteDshGatewayAsync(plan, root, providers, gatewayBase, cancellationToken);
        }

        ConfigFileSafety.WriteAllText(_dshSettings, YamlTree.Save(root));
        return result;
    }

    /// <summary>agent-default-model 指向本次被移除的 provider 时一并清掉，避免悬空；其余默认选择不动。</summary>
    private static void RemoveDanglingDshDefault(YamlMappingNode root, YamlMappingNode providers, IReadOnlyList<string> removedIds)
    {
        if (removedIds.Count == 0) return;
        if (root.Children.All(pair => (pair.Key as YamlScalarNode)?.Value != "agent-default-model")) return;
        var agentDefault = (YamlMappingNode)root.Children.First(pair => (pair.Key as YamlScalarNode)?.Value == "agent-default-model").Value;
        var providerId = YamlTree.Scalar(agentDefault, "provider");
        if (providerId is not null && removedIds.Contains(providerId))
        {
            YamlTree.Remove(root, "agent-default-model");
        }
    }

    /// <summary>直连模式：每个上游一个协议组，密钥写入独立的凭据引用；网关组一并移除。</summary>
    private async Task<ClientSyncResult> WriteDshDirectAsync(
        ClientSyncPlan plan, YamlMappingNode root, YamlMappingNode providers, string gatewayBase, CancellationToken cancellationToken)
    {
        var credentials = await YamlTree.ReadRootAsync(_dshCredentials, cancellationToken);
        var refs = YamlTree.GetOrCreateMapping(credentials, "refs");
        var keep = new List<string>();
        foreach (var upstream in plan.Upstreams)
        {
            var id = DirectId(upstream.Key);
            keep.Add(id);
            var envName = DirectEnvName(id);
            var api = upstream.Kind == ProxyProviderKind.Claude ? "anthropic-messages" : "openai-responses";
            var baseUrl = upstream.Kind == ProxyProviderKind.Claude ? upstream.BaseUrl.TrimEnd('/') : EnsureV1(upstream.BaseUrl);
            WriteDshGroup(providers, id, api, upstream.DisplayName, envName, baseUrl, [.. upstream.Models.Select(WithoutAlias)], removeWhenEmpty: true);
            refs.Children[YamlTree.Key(envName)] = YamlTree.Text(upstream.ApiKey);
        }
        var removed = DshGatewayProviders(providers, gatewayBase).Select(entry => entry.Id).Distinct().ToList();
        foreach (var id in removed)
        {
            YamlTree.Remove(providers, id);
        }
        foreach (var (id, _) in providers.Children.ToList())
        {
            if ((id as YamlScalarNode)?.Value is { } name && name.StartsWith("direct-", StringComparison.Ordinal) && !keep.Contains(name))
            {
                YamlTree.Remove(providers, name);
                removed.Add(name);
            }
        }
        RemoveDanglingDshDefault(root, providers, removed);
        ConfigFileSafety.WriteAllText(_dshCredentials, YamlTree.Save(credentials));
        return BuildResult("direct", [_dshSettings, _dshCredentials], plan);
    }

    private async Task<ClientSyncResult> WriteDshGatewayAsync(
        ClientSyncPlan plan, YamlMappingNode root, YamlMappingNode providers, string gatewayBase, CancellationToken cancellationToken)
    {
        // dsh 支持 anthropic-messages / openai-completions / openai-responses：
        // Claude 上游走 anthropic-messages（原生协议，baseURL 为网关根地址），
        // Codex 上游走 openai-responses（baseURL 带 /v1）。
        var anthropicModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Claude).Select(model => model.Config).ToList();
        var responsesModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Codex).Select(model => model.Config).ToList();
        var existing = DshGatewayProviders(providers, gatewayBase).ToList();
        // anthropic 组优先复用既有 anthropic-messages 条目；否则升级复用旧 openai-completions 条目的 id
        var anthropicId = existing.FirstOrDefault(entry => entry.Api == "anthropic-messages").Id
            ?? existing.FirstOrDefault(entry => entry.Api == "openai-completions").Id
            ?? plan.ProviderId;
        var responsesId = existing.FirstOrDefault(entry => entry.Api == "openai-responses").Id ?? $"{plan.ProviderId}-responses";
        var envName = existing.FirstOrDefault(entry => entry.EnvName is not null).EnvName ?? DefaultDshEnvName;

        WriteDshGroup(providers, anthropicId, "anthropic-messages", "CLIProxyAPI", envName, gatewayBase, anthropicModels, removeWhenEmpty: existing.Any(entry => entry.Id == anthropicId));
        WriteDshGroup(providers, responsesId, "openai-responses", "CLIProxyAPI Responses", envName, $"{gatewayBase}/v1", responsesModels, removeWhenEmpty: existing.Any(entry => entry.Id == responsesId));
        var removed = new List<string>();
        foreach (var (id, _) in providers.Children.ToList())
        {
            if ((id as YamlScalarNode)?.Value is { } name && name.StartsWith("direct-", StringComparison.Ordinal))
            {
                YamlTree.Remove(providers, name);
                removed.Add(name);
            }
        }
        RemoveDanglingDshDefault(root, providers, removed);

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
        // 直连模式下各上游的模型命名空间相互独立，按组内计数求和
        ModelCount = plan.Upstreams.Count > 0
            ? plan.Upstreams.Sum(upstream => upstream.Models.Count)
            : plan.Models.Select(model => model.Config.GetId()).Distinct(StringComparer.OrdinalIgnoreCase).Count()
    };

    /// <summary>直连条目的确定性 id：上游标识（api-key|base-url）的短哈希，同步多次结果稳定。</summary>
    public static string DirectId(string upstreamKey) =>
        "direct-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(upstreamKey)))[..8].ToLowerInvariant();

    private static string DirectEnvName(string directId) =>
        "DIRECT_" + directId["direct-".Length..].ToUpperInvariant() + "_API_KEY";

    /// <summary>OpenAI 系端点实测都挂在 /v1 之下（Heju 等地址本身已带 /v1 的原样保留）。</summary>
    private static string EnsureV1(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/v1";
    }

    /// <summary>直连时模型用上游真实名；别名只对网关路由有意义。</summary>
    private static ProxyModelConfig WithoutAlias(ProxyModelConfig model) => new()
    {
        Name = model.Name,
        Alias = null,
        ThinkingLevels = model.ThinkingLevels,
        MaxContextLength = model.MaxContextLength,
        InputModalities = model.InputModalities,
        OutputModalities = model.OutputModalities,
        Cost = model.Cost
    };

    /// <summary>移除指向网关的 provider 条目（全部→指定切换后，网关别名不再可用），返回被移除的条目 id。</summary>
    private static List<string> RemoveGatewayEntries(JsonObject providers, string gatewayBaseUrl)
    {
        var removed = new List<string>();
        if (string.IsNullOrWhiteSpace(gatewayBaseUrl)) return removed;
        foreach (var (id, node) in providers.ToList())
        {
            if (node is not JsonObject entry) continue;
            var existing = (string?)entry["options"]?["baseURL"];
            if (existing is null || !existing.TrimEnd('/').StartsWith(gatewayBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            providers.Remove(id);
            removed.Add(id);
        }
        return removed;
    }

    /// <summary>移除本次未保留的 direct- 前缀条目，返回被移除的条目 id。</summary>
    private static List<string> RemoveDirectEntries(JsonObject providers, IReadOnlyList<string> keep)
    {
        var removed = new List<string>();
        foreach (var (id, _) in providers.ToList())
        {
            if (id.StartsWith("direct-", StringComparison.Ordinal) && !keep.Contains(id))
            {
                providers.Remove(id);
                removed.Add(id);
            }
        }
        return removed;
    }

    /// <summary>默认模型指向本次被移除的 provider 时一并清掉引用，避免悬空；其余默认选择不动。</summary>
    private static void RemoveDanglingDefaultModel(JsonObject root, IReadOnlyList<string> removedIds)
    {
        if (removedIds.Count == 0 || (string?)root["model"] is not { } reference) return;
        var separator = reference.IndexOf('/');
        if (separator <= 0) return;
        if (removedIds.Contains(reference[..separator])) root.Remove("model");
    }

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
