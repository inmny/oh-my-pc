using System.Text.Json.Nodes;
using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

/// <summary>
/// 解析 models.dev 的 api.json（{providerId: {models: {modelId: {...}}}}）为归一化的元数据字典。
/// 精确 id 与去掉 org/ 前缀的短 id 都会登记；同一 id 多 provider 重复时优先保留费率字段更完整的条目。
/// </summary>
public static class ModelMetadataParser
{
    public static IReadOnlyDictionary<string, ModelMetadata> Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var exact = new Dictionary<string, ModelMetadata>(StringComparer.OrdinalIgnoreCase);
        var suffixed = new Dictionary<string, ModelMetadata>(StringComparer.OrdinalIgnoreCase);
        if (root is null) return exact;

        foreach (var (_, providerNode) in root)
        {
            if (providerNode?["models"] is not JsonObject models) continue;
            foreach (var (modelId, modelNode) in models)
            {
                if (modelNode is null || string.IsNullOrWhiteSpace(modelId)) continue;
                var metadata = ParseModel(modelId, modelNode);
                Merge(exact, modelId, metadata);
                var separator = modelId.LastIndexOf('/');
                if (separator >= 0 && separator + 1 < modelId.Length)
                {
                    Merge(suffixed, modelId[(separator + 1)..], metadata);
                }
            }
        }

        foreach (var (id, metadata) in suffixed)
        {
            exact.TryAdd(id, metadata);
        }
        return exact;
    }

    /// <summary>
    /// 同名 id 多 provider 重复时择优：先比正费率字段数（订阅套餐的全 0 费率不算有效信息，
    /// 官方牌价优先于它）；正数字段数相同再比费率字段数（含缓存价的完整费率表优先）；
    /// 仍相同则保留既有条目。
    /// </summary>
    private static void Merge(Dictionary<string, ModelMetadata> target, string id, ModelMetadata metadata)
    {
        if (target.TryGetValue(id, out var existing))
        {
            var existingPositive = PositiveFieldCount(existing.Cost);
            var candidatePositive = PositiveFieldCount(metadata.Cost);
            if (existingPositive > candidatePositive) return;
            if (existingPositive == candidatePositive && CostFieldCount(existing.Cost) >= CostFieldCount(metadata.Cost)) return;
        }
        target[id] = metadata;
    }

    private static int CostFieldCount(ProxyModelCost cost) =>
        (cost.Input is null ? 0 : 1) + (cost.Output is null ? 0 : 1)
        + (cost.CacheRead is null ? 0 : 1) + (cost.CacheWrite is null ? 0 : 1);

    private static int PositiveFieldCount(ProxyModelCost cost) =>
        (cost.Input > 0 ? 1 : 0) + (cost.Output > 0 ? 1 : 0)
        + (cost.CacheRead > 0 ? 1 : 0) + (cost.CacheWrite > 0 ? 1 : 0);

    /// <summary>思考档位与推理变体的常见尾部后缀（如 claude-opus-4-6-thinking、gemini-3.1-pro-high）。</summary>
    private static readonly HashSet<string> VariantSuffixes =
        [.. ProxyCatalog.ThinkingLevels, "thinking", "nothinking"];

    /// <summary>按 id 查找元数据：先精确匹配，未命中时逐个剥离尾部的档位/变体后缀再试。</summary>
    public static ModelMetadata? Find(IReadOnlyDictionary<string, ModelMetadata> catalog, string id)
    {
        if (catalog.TryGetValue(id, out var metadata)) return metadata;
        var candidate = id;
        while (true)
        {
            var index = candidate.LastIndexOf('-');
            if (index <= 0 || !VariantSuffixes.Contains(candidate[(index + 1)..])) return null;
            candidate = candidate[..index];
            if (catalog.TryGetValue(candidate, out metadata)) return metadata;
        }
    }

    /// <summary>
    /// 模型名归一：别名反查 → 目录精确 → 目录剥后缀 → 原样返回。
    /// 用量统计把各客户端上报的别名/变体名统一到目录标准名，费率与分组因此共享同一条目。
    /// </summary>
    public static string Canonicalize(
        IReadOnlyDictionary<string, ModelMetadata> catalog,
        string id,
        IReadOnlyDictionary<string, string>? aliasToName = null)
    {
        if (aliasToName is not null && aliasToName.TryGetValue(id, out var realName))
        {
            id = realName;
        }
        return Find(catalog, id) is { } metadata ? metadata.Id : id;
    }

    private static ModelMetadata ParseModel(string id, JsonNode node) => new()
    {
        Id = id,
        DisplayName = (string?)node["name"],
        ContextWindow = ToInt64(node["limit"]?["context"]),
        InputModalities = FilterModalities(node["modalities"]?["input"] as JsonArray),
        OutputModalities = FilterModalities(node["modalities"]?["output"] as JsonArray),
        ThinkingLevels = ParseLevels(node["reasoning_options"] as JsonArray),
        Cost = ParseCost(node["cost"] as JsonObject)
    };

    /// <summary>把 effort 型思考档位映射到本应用取值：none→off，过滤未知档位并按强度排序。</summary>
    private static IReadOnlyList<string> ParseLevels(JsonArray? options)
    {
        var values = options?.FirstOrDefault(option =>
            (string?)option?["type"] == "effort")?["values"] as JsonArray;
        if (values is null) return [];
        var levels = values
            .Select(value => (string?)value)
            .Select(value => value == "none" ? "off" : value)
            .Where(value => value is not null && ProxyCatalog.ThinkingLevels.Contains(value!))
            .Select(value => value!);
        return ProxyMappers.OrderLevels(levels);
    }

    private static IReadOnlyList<string> FilterModalities(JsonArray? array) =>
        array is null
            ? []
            : [.. array.Select(value => (string?)value)
                .Where(value => value is not null)
                .Select(value => value!)
                .Where(value => ProxyCatalog.Modalities.Contains(value))];

    private static ProxyModelCost ParseCost(JsonObject? cost) => new()
    {
        Input = ToDecimal(cost?["input"]),
        Output = ToDecimal(cost?["output"]),
        CacheRead = ToDecimal(cost?["cache_read"]),
        CacheWrite = ToDecimal(cost?["cache_write"])
    };

    private static long? ToInt64(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : null;

    private static decimal? ToDecimal(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;
}
