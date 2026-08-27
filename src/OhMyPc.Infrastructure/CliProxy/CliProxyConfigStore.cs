using System.Globalization;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>
/// CLIProxyAPI config.yaml 的结构化编辑器：
/// 只读写本应用管理的段落（上游 provider、路由、下游 api-keys），
/// 其余字段（凭据并发、日志、TLS 等）在保存时原样保留。
/// </summary>
public sealed class CliProxyConfigStore(string? configPath = null, string? authDirectory = null) : IProxyConfigStore
{
    private const string ClaudeSection = "claude-api-key";
    private const string CodexSection = "codex-api-key";

    private readonly string _configPath = configPath ?? CliProxyPaths.ConfigPath;
    private readonly string _authDirectory = authDirectory ?? CliProxyPaths.AuthDirectory;

    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public async Task<bool> EnsureConfigAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_configPath)) return false;
        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_configPath)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            Directory.CreateDirectory(_authDirectory);
            var initial =
                $"""
                host: 127.0.0.1
                port: 8317
                auth-dir: ./oauth
                api-keys:
                  - '{ProxyCatalog.DefaultApiKey}'
                request-retry: 3
                routing:
                  strategy: {ProxyCatalog.StrategyRoundRobin}
                """;
            ConfigFileSafety.WriteAllText(_configPath, initial);
            return true;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public async Task<ProxyConfigSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException("未找到 CLIProxyAPI 配置文件。", _configPath);
        var root = await YamlTree.ReadRootAsync(_configPath, cancellationToken);
        var providers = new List<ProxyProviderConfig>();
        providers.AddRange(LoadProviders(root, ClaudeSection, ProxyProviderKind.Claude));
        providers.AddRange(LoadProviders(root, CodexSection, ProxyProviderKind.Codex));

        var routing = new ProxyRoutingConfig
        {
            Strategy = YamlTree.Scalar(GetMapping(root, "routing"), "strategy") ?? ProxyCatalog.StrategyRoundRobin,
            SessionAffinity = YamlTree.TryGetBoolean(GetMapping(root, "routing"), "session-affinity", false),
            RequestRetry = YamlTree.GetInt32(root, "request-retry", 3),
            MaxRetryInterval = YamlTree.GetInt32(root, "max-retry-interval", 30)
        };
        var access = new ProxyAccessConfig
        {
            Host = YamlTree.Scalar(root, "host") ?? "127.0.0.1",
            Port = YamlTree.GetInt32(root, "port", 8317),
            ApiKeys = [.. YamlTree.StringList(root, "api-keys")]
        };
        return new ProxyConfigSnapshot { Providers = providers, Routing = routing, Access = access };
    }

    public async Task SaveAsync(ProxyConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await EnsureConfigAsync(cancellationToken);
        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            // 重新加载文件后只改管理的键，避免覆盖其他工具写入的未知字段。
            var root = await YamlTree.ReadRootAsync(_configPath, cancellationToken);
            YamlTree.SetScalar(root, "host", snapshot.Access.Host);
            YamlTree.SetScalar(root, "port", snapshot.Access.Port);
            YamlTree.SetStringList(root, "api-keys", snapshot.Access.ApiKeys);
            YamlTree.SetScalar(root, "request-retry", snapshot.Routing.RequestRetry);
            YamlTree.SetScalar(root, "max-retry-interval", snapshot.Routing.MaxRetryInterval);
            var routing = YamlTree.GetOrCreateMapping(root, "routing");
            YamlTree.SetScalar(routing, "strategy", snapshot.Routing.Strategy);
            routing.Children[YamlTree.Key("session-affinity")] = YamlTree.Plain(snapshot.Routing.SessionAffinity ? "true" : "false");

            SaveProviders(root, ClaudeSection, snapshot.Providers.Where(p => p.Kind == ProxyProviderKind.Claude));
            SaveProviders(root, CodexSection, snapshot.Providers.Where(p => p.Kind == ProxyProviderKind.Codex));
            ConfigFileSafety.WriteAllText(_configPath, YamlTree.Save(root));
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static YamlMappingNode GetMapping(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(YamlTree.Key(key), out var node) && node is YamlMappingNode mapping
            ? mapping
            : new YamlMappingNode();

    private static IEnumerable<ProxyProviderConfig> LoadProviders(YamlMappingNode root, string section, ProxyProviderKind kind) =>
        YamlTree.Sequence(root, section)?.Children.OfType<YamlMappingNode>().Select(node => new ProxyProviderConfig
        {
            Kind = kind,
            ApiKey = YamlTree.Scalar(node, "api-key") ?? "",
            BaseUrl = YamlTree.Scalar(node, "base-url") ?? "",
            Remark = YamlTree.Scalar(node, "remark"),
            Priority = YamlTree.GetInt32OrNull(node, "priority"),
            Models = [.. YamlTree.Sequence(node, "models")?.Children.OfType<YamlMappingNode>().Select(LoadModel) ?? []]
        }) ?? [];

    private static ProxyModelConfig LoadModel(YamlMappingNode node) => new()
    {
        Name = YamlTree.Scalar(node, "name") ?? "",
        Alias = YamlTree.Scalar(node, "alias"),
        MaxContextLength = YamlTree.GetInt64OrNull(node, "max-context-length"),
        ThinkingLevels = [.. YamlTree.StringList(GetMapping(node, "thinking"), "levels")],
        InputModalities = [.. YamlTree.StringList(node, "input-modalities")],
        OutputModalities = [.. YamlTree.StringList(node, "output-modalities")],
        Cost = LoadCost(YamlTree.Mapping(node, "cost"))
    };

    private static ProxyModelCost? LoadCost(YamlMappingNode? node) =>
        node is null ? null : new ProxyModelCost
        {
            Input = YamlTree.GetDecimalOrNull(node, "input"),
            Output = YamlTree.GetDecimalOrNull(node, "output"),
            CacheRead = YamlTree.GetDecimalOrNull(node, "cache-read"),
            CacheWrite = YamlTree.GetDecimalOrNull(node, "cache-write")
        };

    private static void SaveProviders(YamlMappingNode root, string section, IEnumerable<ProxyProviderConfig> providers)
    {
        var list = providers.ToList();
        if (list.Count == 0)
        {
            YamlTree.Remove(root, section);
            return;
        }
        var existing = YamlTree.Sequence(root, section);
        var rebuilt = new YamlSequenceNode(list.Select(provider => UpsertProvider(existing, provider)));
        root.Children[YamlTree.Key(section)] = rebuilt;
    }

    /// <summary>按 api-key + base-url 匹配既有条目：命中则只改已知键（保留 weight、proxy-url 等），否则新建。</summary>
    private static YamlMappingNode UpsertProvider(YamlSequenceNode? existing, ProxyProviderConfig provider)
    {
        var node = existing?.Children.OfType<YamlMappingNode>().FirstOrDefault(candidate =>
            string.Equals(YamlTree.Scalar(candidate, "api-key"), provider.ApiKey, StringComparison.Ordinal)
            && string.Equals(YamlTree.Scalar(candidate, "base-url"), provider.BaseUrl, StringComparison.Ordinal))
            ?? new YamlMappingNode();
        YamlTree.SetScalar(node, "api-key", provider.ApiKey);
        YamlTree.SetScalar(node, "base-url", provider.BaseUrl);
        if (!string.IsNullOrWhiteSpace(provider.Remark)) YamlTree.SetScalar(node, "remark", provider.Remark.Trim());
        else YamlTree.Remove(node, "remark");
        if (provider.Priority is int priority) YamlTree.SetScalar(node, "priority", priority);
        else YamlTree.Remove(node, "priority");
        node.Children[YamlTree.Key("models")] = BuildModelsNode(YamlTree.Sequence(node, "models"), provider.Models);
        return node;
    }

    private static YamlSequenceNode BuildModelsNode(YamlSequenceNode? existing, IReadOnlyList<ProxyModelConfig> models)
    {
        if (models.Count == 0) return new YamlSequenceNode();
        var rebuilt = models.Select(model => UpsertModel(existing, model)).ToArray();
        return new YamlSequenceNode(rebuilt);
    }

    private static YamlMappingNode UpsertModel(YamlSequenceNode? existing, ProxyModelConfig model)
    {
        var node = existing?.Children.OfType<YamlMappingNode>().FirstOrDefault(candidate =>
            string.Equals(YamlTree.Scalar(candidate, "name"), model.Name, StringComparison.Ordinal))
            ?? new YamlMappingNode();
        YamlTree.SetScalar(node, "name", model.Name);
        if (!string.IsNullOrWhiteSpace(model.Alias)) YamlTree.SetScalar(node, "alias", model.Alias);
        else YamlTree.Remove(node, "alias");
        if (model.MaxContextLength is long context) node.Children[YamlTree.Key("max-context-length")] = YamlTree.Plain(context.ToString(CultureInfo.InvariantCulture));
        else YamlTree.Remove(node, "max-context-length");
        if (model.ThinkingLevels.Count > 0)
        {
            var thinking = YamlTree.GetOrCreateMapping(node, "thinking");
            YamlTree.SetStringList(thinking, "levels", model.ThinkingLevels);
        }
        else YamlTree.Remove(node, "thinking");
        if (model.InputModalities.Count > 0) YamlTree.SetStringList(node, "input-modalities", model.InputModalities);
        else YamlTree.Remove(node, "input-modalities");
        if (model.OutputModalities.Count > 0) YamlTree.SetStringList(node, "output-modalities", model.OutputModalities);
        else YamlTree.Remove(node, "output-modalities");
        SaveCost(node, model.Cost);
        return node;
    }

    /// <summary>费用存于模型条目 cost 键（CLIProxyAPI 忽略）；逐键更新，清空的键会被移除。</summary>
    private static void SaveCost(YamlMappingNode node, ProxyModelCost? cost)
    {
        if (cost is null || cost.IsEmpty)
        {
            YamlTree.Remove(node, "cost");
            return;
        }
        var target = YamlTree.GetOrCreateMapping(node, "cost");
        UpsertDecimal(target, "input", cost.Input);
        UpsertDecimal(target, "output", cost.Output);
        UpsertDecimal(target, "cache-read", cost.CacheRead);
        UpsertDecimal(target, "cache-write", cost.CacheWrite);
    }

    private static void UpsertDecimal(YamlMappingNode map, string key, decimal? value)
    {
        if (value is not null) map.Children[YamlTree.Key(key)] = YamlTree.Plain(value.Value.ToString(CultureInfo.InvariantCulture));
        else YamlTree.Remove(map, key);
    }
}
