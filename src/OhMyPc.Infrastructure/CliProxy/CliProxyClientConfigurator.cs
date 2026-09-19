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
/// 全部采用结构化编辑：只改本应用管理的键，保留各客户端配置中的其余字段（费用配置、用户自建规则等）。
/// </summary>
public sealed class CliProxyClientConfigurator(
    string? zcodeProviderConfig = null,
    string? workbuddyModels = null,
    string? opencodeConfig = null,
    string? dshSettings = null,
    string? dshCredentials = null) : IClientConfigurator
{
    public const string DefaultProviderId = "cli-proxy-api";
    public const string DefaultDshEnvName = "CLIPROXYAPI_API_KEY";
    private const string DisplayName = "CLIProxyAPI";

    private readonly string _zcodeProviderConfig = zcodeProviderConfig ?? ProxyClientPaths.ZcodeProviderConfig;
    private readonly string _workbuddyModels = workbuddyModels ?? ProxyClientPaths.WorkbuddyModels;
    private readonly string _opencodeConfig = opencodeConfig ?? ProxyClientPaths.OpencodeConfig;
    private readonly string _dshSettings = dshSettings ?? ProxyClientPaths.DshSettings;
    private readonly string _dshCredentials = dshCredentials ?? ProxyClientPaths.DshCredentials;

    private const string WorkbuddyVendorMarker = "oh-my-pc";
    private const int WorkbuddyMaxOutputTokens = 128000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Task<ClientSyncResult> SyncAsync(ClientSyncPlan plan, CancellationToken cancellationToken = default) => plan.Client switch
    {
        ProxyClientKind.Zcode => SyncZcodeAsync(plan, cancellationToken),
        ProxyClientKind.Opencode => SyncOpencodeAsync(plan, cancellationToken),
        ProxyClientKind.Dsh => SyncDshAsync(plan, cancellationToken),
        ProxyClientKind.Workbuddy => SyncWorkbuddyAsync(plan, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Client, "未知的客户端类型")
    };

    private static bool DirectoryExistsFor(string path) =>
        Directory.Exists(Path.GetDirectoryName(path));

    private async Task<ClientSyncResult> SyncZcodeAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        if (!DirectoryExistsFor(_zcodeProviderConfig))
            throw new InvalidOperationException("未找到 zcode 配置目录（~/.zcode/v2）。");

        // zcode 桌面端 3.12+ 的个人 Provider 唯一来源是 provider_config.json（schemaVersion 1），
        // 旧的 v2/config.json 与 cli/config.json 的 provider 段已不被读取。
        var root = await ReadJsonObjectAsync(_zcodeProviderConfig, cancellationToken);
        NormalizeProviderConfigSkeleton(root);
        var config = (JsonObject)root["config"]!;
        var rules = (JsonArray)config["providerConfigRules"]!["providerRules"]!;
        var modelRules = (JsonArray)config["modelConfigRules"]!["providerModelRules"]!;

        var (removedIds, modelCount, providerId) = plan.Upstreams.Count > 0
            ? SyncZcodeDirectRules(rules, modelRules, plan)
            : SyncZcodeGatewayRules(rules, modelRules, plan);

        // 默认模型选择指向被移除的 provider 时一并清掉，避免悬空；其模型覆盖规则同步清理
        if (removedIds.Count > 0)
        {
            foreach (var node in modelRules.Where(node => node is JsonObject entry
                     && removedIds.Contains((string?)entry["providerId"] ?? "")).ToList())
            {
                modelRules.Remove(node);
            }
            if (config["defaultModelSelection"] is JsonObject selection
                && (string?)selection["providerId"] is { } selected
                && removedIds.Contains(selected))
            {
                config.Remove("defaultModelSelection");
            }
        }

        await WriteAsync(_zcodeProviderConfig, root, cancellationToken);
        return new ClientSyncResult { WrittenFiles = [_zcodeProviderConfig], ProviderId = providerId, ModelCount = modelCount };
    }

    /// <summary>补齐 provider_config.json 的必需骨架；缺失的键按 zcode 的 schema 默认值创建。</summary>
    private static void NormalizeProviderConfigSkeleton(JsonObject root)
    {
        if ((int?)root["schemaVersion"] is null) root["schemaVersion"] = 1;
        if (root["config"] is not JsonObject config) root["config"] = config = new JsonObject();
        if (config["providerConfigRules"] is not JsonObject rulesConfig) config["providerConfigRules"] = rulesConfig = new JsonObject();
        if (rulesConfig["providerRules"] is not JsonArray rules) rulesConfig["providerRules"] = rules = new JsonArray();
        if (config["modelConfigRules"] is not JsonObject modelConfig) config["modelConfigRules"] = modelConfig = new JsonObject();
        if (modelConfig["providerModelRules"] is not JsonArray providerModelRules) modelConfig["providerModelRules"] = providerModelRules = new JsonArray();
        if (modelConfig["manualProviderModelRules"] is not JsonArray) modelConfig["manualProviderModelRules"] = new JsonArray();
    }

    /// <summary>网关模式：Claude 上游一组 anthropic-messages（根地址），其余上游经 CPA 统一一组 openai-responses（/v1）。</summary>
    private (IReadOnlyList<string> Removed, int ModelCount, string ProviderId) SyncZcodeGatewayRules(
        JsonArray rules, JsonArray modelRules, ClientSyncPlan plan)
    {
        var anthropicModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Claude).Select(model => model.Config).ToList();
        var responsesModels = plan.Models.Where(model => model.Kind != ProxyProviderKind.Claude).Select(model => model.Config).ToList();

        var anthropicId = DetectRuleId(rules, plan.BaseUrl, "anthropic-messages") ?? plan.ProviderId;
        var responsesId = DetectRuleId(rules, plan.BaseUrl, "openai-responses") ?? $"{plan.ProviderId}-codex";

        UpsertZcodeRule(rules, anthropicId, DisplayName, plan.ApiKey, "anthropic-messages", plan.BaseUrl,
            anthropicModels.Select(model => model.GetId()).ToList());
        UpsertZcodeRule(rules, responsesId, $"{DisplayName} Codex", plan.ApiKey, "openai-responses", EnsureV1(plan.BaseUrl),
            responsesModels.Select(model => model.GetId()).ToList());
        SyncZcodeModelRules(modelRules, anthropicId, anthropicModels);
        SyncZcodeModelRules(modelRules, responsesId, responsesModels);

        // 两个网关组之外：全部 direct 条目移除（全部→指定切换后不再可用）；指向网关的其余旧条目一并清理
        var gatewayBase = plan.BaseUrl.TrimEnd('/');
        var removed = RemoveRules(rules, rule =>
        {
            var id = (string?)rule["providerId"];
            if (id is null || id == anthropicId || id == responsesId) return false;
            return id.StartsWith("direct-", StringComparison.Ordinal) || IsGatewayRule(rule, gatewayBase);
        });
        return (removed, plan.Models.Select(model => model.Config.GetId()).Distinct(StringComparer.OrdinalIgnoreCase).Count(), anthropicId);
    }

    /// <summary>直连模式：每个上游一个 provider 规则，指向其真实地址与密钥，模型用上游真实名。</summary>
    private (IReadOnlyList<string> Removed, int ModelCount, string ProviderId) SyncZcodeDirectRules(
        JsonArray rules, JsonArray modelRules, ClientSyncPlan plan)
    {
        var keep = new List<string>();
        foreach (var upstream in plan.Upstreams)
        {
            var id = DirectId(upstream.Key);
            keep.Add(id);
            // anthropic SDK 自拼 /v1/messages 用根地址；openai 系端点在 /v1 下，必须自带 /v1
            var baseUrl = upstream.Kind == ProxyProviderKind.Claude ? upstream.BaseUrl.TrimEnd('/') : EnsureV1(upstream.BaseUrl);
            var apiType = upstream.Kind switch
            {
                ProxyProviderKind.Claude => "anthropic-messages",
                ProxyProviderKind.OpenAiCompatible => "openai-chat-completions",
                _ => "openai-responses"
            };
            var modelIds = upstream.Models.Select(model => WithoutAlias(model).GetId()).ToList();
            UpsertZcodeRule(rules, id, upstream.DisplayName, upstream.ApiKey, apiType, baseUrl, modelIds);
            SyncZcodeModelRules(modelRules, id, [.. upstream.Models.Select(WithoutAlias)]);
        }

        // 未保留的 direct 条目与指向网关的旧条目一并移除
        var gatewayBase = plan.BaseUrl.TrimEnd('/');
        var removed = RemoveRules(rules, rule =>
        {
            var id = (string?)rule["providerId"];
            if (id is null) return false;
            return id.StartsWith("direct-", StringComparison.Ordinal) && !keep.Contains(id) || IsGatewayRule(rule, gatewayBase);
        });
        return (removed, BuildResult("direct", [], plan).ModelCount, "direct");
    }

    /// <summary>按 zcode 的 providerRules 结构插入或覆盖一条规则；模型清单以本次同步为准。</summary>
    private static void UpsertZcodeRule(
        JsonArray rules, string providerId, string displayName, string apiKey, string apiType, string baseUrl,
        IReadOnlyList<string> modelIds)
    {
        var rule = rules.FirstOrDefault(node =>
            node is JsonObject entry && (string?)entry["providerId"] == providerId) as JsonObject ?? new JsonObject();
        if (rule["providerId"] is null) rules.Add(rule);
        rule["providerId"] = providerId;
        rule["providerName"] = displayName;
        rule["enabled"] = true;
        var config = GetOrCreateObject(rule, "config");
        config["group"] = "standard-personal";
        var access = GetOrCreateObject(config, "access");
        access["type"] = "api-key";
        access["apiKey"] = apiKey;
        var api = GetOrCreateObject(config, "api");
        api["type"] = apiType;
        api["baseUrl"] = baseUrl;
        config["personalModelIds"] = ToNode(modelIds);
        config["modelOrder"] = ToNode(modelIds);
    }

    /// <summary>上下文长度覆盖规则与本次同步的模型对齐：计划内模型写入，计划外模型的规则清掉。</summary>
    private static void SyncZcodeModelRules(
        JsonArray modelRules, string providerId, IReadOnlyList<ProxyModelConfig> models)
    {
        var ids = models.Select(model => model.GetId()).ToHashSet(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model.MaxContextLength is not { } contextWindow) continue;
            JsonObject entry = modelRules.FirstOrDefault(node =>
                node is JsonObject candidate
                && (string?)candidate["providerId"] == providerId
                && (string?)candidate["modelId"] == model.GetId()) as JsonObject ?? new JsonObject();
            if (entry["modelId"] is null)
            {
                modelRules.Add(entry);
                entry["modelId"] = model.GetId();
                entry["providerId"] = providerId;
            }
            var config = GetOrCreateObject(entry, "config");
            var properties = GetOrCreateObject(config, "properties");
            properties["contextWindow"] = contextWindow;
        }
        foreach (var node in modelRules.Where(node => node is JsonObject entry
                 && (string?)entry["providerId"] == providerId
                 && !ids.Contains((string?)entry["modelId"] ?? "")).ToList())
        {
            modelRules.Remove(node);
        }
    }

    /// <summary>移除命中的规则，返回被移除规则的 providerId。</summary>
    private static List<string> RemoveRules(JsonArray rules, Func<JsonObject, bool> shouldRemove)
    {
        var removed = new List<string>();
        foreach (var node in rules.Where(node => node is JsonObject rule && shouldRemove(rule)).ToList())
        {
            removed.Add((string?)node["providerId"] ?? "");
            rules.Remove(node);
        }
        return removed;
    }

    /// <summary>规则指向网关地址（根地址或 /v1 均算）。</summary>
    private static bool IsGatewayRule(JsonObject rule, string gatewayBase) =>
        (string?)rule["config"]?["api"]?["baseUrl"] is { } baseUrl
        && baseUrl.TrimEnd('/').StartsWith(gatewayBase, StringComparison.OrdinalIgnoreCase);

    /// <summary>查找已指向本网关且协议一致的既有规则，复用其 providerId（如旧 cpa-gui 条目）。</summary>
    private static string? DetectRuleId(JsonArray rules, string baseUrl, string apiType)
    {
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            if ((string?)rule["config"]?["api"]?["type"] != apiType) continue;
            var existing = (string?)rule["config"]?["api"]?["baseUrl"];
            if (existing is not null && existing.TrimEnd('/').StartsWith(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return (string?)rule["providerId"];
            }
        }
        return null;
    }

    /// <summary>
    /// workbuddy 的自定义模型是扁平清单（~/.workbuddy/models.json），协议固定为 OpenAI Chat Completions：
    /// url 填基础地址（workbuddy 自动补 /chat/completions）。本应用写入的条目以 vendor "oh-my-pc"
    /// 标识所有权：重同步时移除计划外条目，用户手加的条目原样保留。
    /// </summary>
    private async Task<ClientSyncResult> SyncWorkbuddyAsync(ClientSyncPlan plan, CancellationToken cancellationToken)
    {
        if (!DirectoryExistsFor(_workbuddyModels))
            throw new InvalidOperationException("未找到 workbuddy 配置目录（~/.workbuddy）。");

        var models = await ReadJsonArrayAsync(_workbuddyModels, cancellationToken);
        var entries = new List<(string Id, JsonObject Entry)>();
        if (plan.Upstreams.Count > 0)
        {
            foreach (var upstream in plan.Upstreams)
            {
                // chat 端点在 /v1 之下，基础地址必须自带 /v1；模型用上游真实名
                var url = EnsureV1(upstream.BaseUrl);
                foreach (var model in upstream.Models.Select(WithoutAlias))
                {
                    entries.Add((model.GetId(), BuildWorkbuddyEntry(model, url, upstream.ApiKey)));
                }
            }
        }
        else
        {
            // 网关模式保留别名作为客户端侧 id；兼容上游由 CPA 完成协议转换，全部走网关 /v1
            var url = EnsureV1(plan.BaseUrl);
            foreach (var model in plan.Models.Select(model => model.Config).DistinctBy(model => model.GetId(), StringComparer.Ordinal))
            {
                entries.Add((model.GetId(), BuildWorkbuddyEntry(model, url, plan.ApiKey)));
            }
        }

        var keep = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in models.Where(node => node is JsonObject entry
                 && (string?)entry["vendor"] == WorkbuddyVendorMarker
                 && !keep.Contains((string?)entry["id"] ?? "")).ToList())
        {
            models.Remove(node);
        }
        foreach (var (id, entry) in entries)
        {
            entry["id"] = id;
            var index = models.ToList().FindIndex(node =>
                node is JsonObject existing && (string?)existing["id"] == id);
            if (index >= 0) models[index] = entry;
            else models.Add(entry);
        }

        await WriteArrayAsync(_workbuddyModels, models, cancellationToken);
        return new ClientSyncResult
        {
            WrittenFiles = [_workbuddyModels],
            ProviderId = plan.Upstreams.Count > 0 ? "direct" : plan.ProviderId,
            ModelCount = keep.Count
        };
    }

    private static JsonObject BuildWorkbuddyEntry(ProxyModelConfig model, string url, string apiKey)
    {
        var id = model.GetId();
        var entry = new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["vendor"] = WorkbuddyVendorMarker,
            ["url"] = url,
            ["apiKey"] = apiKey,
            ["supportsToolCall"] = true,
            ["supportsImages"] = model.InputModalities.Contains("image"),
            ["supportsReasoning"] = ProxyMappers.OrderLevels(model.ThinkingLevels).Count > 0,
            ["maxOutputTokens"] = WorkbuddyMaxOutputTokens
        };
        if (model.MaxContextLength is { } contextWindow) entry["maxInputTokens"] = contextWindow;
        return entry;
    }

    /// <summary>workbuddy 的 models.json 是顶层模型数组；缺失或空文件按空清单处理。</summary>
    private static async Task<JsonArray> ReadJsonArrayAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new JsonArray();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return node switch
        {
            JsonArray array => array,
            null => new JsonArray(),
            _ => throw new InvalidOperationException($"客户端配置文件格式异常：{path}")
        };
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
            var api = upstream.Kind switch
            {
                ProxyProviderKind.Claude => "anthropic-messages",
                ProxyProviderKind.OpenAiCompatible => "openai-completions",
                _ => "openai-responses"
            };
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
        // Codex 上游走 openai-responses（baseURL 带 /v1），
        // OpenAI 兼容上游走 openai-completions（原生 chat 协议，baseURL 带 /v1）。
        var anthropicModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Claude).Select(model => model.Config).ToList();
        var responsesModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.Codex).Select(model => model.Config).ToList();
        var completionsModels = plan.Models.Where(model => model.Kind == ProxyProviderKind.OpenAiCompatible).Select(model => model.Config).ToList();
        var existing = DshGatewayProviders(providers, gatewayBase).ToList();
        // anthropic 组优先复用既有 anthropic-messages 条目；否则升级复用旧 openai-completions 条目的 id
        var anthropicId = existing.FirstOrDefault(entry => entry.Api == "anthropic-messages").Id
            ?? existing.FirstOrDefault(entry => entry.Api == "openai-completions").Id
            ?? plan.ProviderId;
        var responsesId = existing.FirstOrDefault(entry => entry.Api == "openai-responses").Id ?? $"{plan.ProviderId}-responses";
        var completionsId = existing.FirstOrDefault(entry => entry.Api == "openai-completions" && entry.Id != anthropicId).Id
            ?? $"{plan.ProviderId}-completions";
        var envName = existing.FirstOrDefault(entry => entry.EnvName is not null).EnvName ?? DefaultDshEnvName;

        WriteDshGroup(providers, anthropicId, "anthropic-messages", "CLIProxyAPI", envName, gatewayBase, anthropicModels, removeWhenEmpty: existing.Any(entry => entry.Id == anthropicId));
        WriteDshGroup(providers, responsesId, "openai-responses", "CLIProxyAPI Responses", envName, $"{gatewayBase}/v1", responsesModels, removeWhenEmpty: existing.Any(entry => entry.Id == responsesId));
        WriteDshGroup(providers, completionsId, "openai-completions", "CLIProxyAPI Completions", envName, $"{gatewayBase}/v1", completionsModels, removeWhenEmpty: existing.Any(entry => entry.Id == completionsId));
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

    private static async Task WriteArrayAsync(string path, JsonArray array, CancellationToken cancellationToken)
    {
        await Task.Run(() => ConfigFileSafety.WriteAllText(path, array.ToJsonString(JsonOptions)), cancellationToken);
    }
}
