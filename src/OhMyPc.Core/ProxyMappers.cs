using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

/// <summary>把 CLIProxyAPI 的模型定义映射为 zcode / opencode / dsh 客户端配置中的模型结构（纯字典，无 IO）。</summary>
public static class ProxyMappers
{
    public const long DefaultContextWindow = 272000;
    public const long ZcodeOutputLimit = 128000;
    public const long OpencodeOutputLimit = 131072;
    public const long DshMaxTokens = 128000;

    private static readonly IReadOnlyDictionary<string, int> LevelRank = new Dictionary<string, int>
    {
        ["off"] = 1,
        ["low"] = 2,
        ["medium"] = 3,
        ["high"] = 4,
        ["xhigh"] = 5,
        ["max"] = 6
    };

    /// <summary>按强度从小到大排序，过滤未知档位。</summary>
    public static IReadOnlyList<string> OrderLevels(IEnumerable<string> levels) =>
        levels.Where(LevelRank.ContainsKey).OrderBy(level => LevelRank[level]).ToArray();

    /// <summary>默认思考档位取所选档位中强度最高的一个；仅选中 off 时视为不支持思考。</summary>
    public static string? GetDefaultVariant(IEnumerable<string> levels)
    {
        var ordered = OrderLevels(levels);
        if (ordered.Count == 0) return null;
        return ordered[^1] == "off" ? null : ordered[^1];
    }

    private static List<string> NormalizeModalities(IReadOnlyList<string> configured, bool isInput) =>
        configured.Count > 0 ? [.. configured] : isInput ? ["text", "image"] : ["text"];

    /// <summary>按客户端键名生成费用子对象；全部为空时返回 null（不写入）。</summary>
    private static Dictionary<string, object?>? BuildCost(ProxyModelConfig model, string cacheReadKey, string cacheWriteKey)
    {
        var cost = model.Cost;
        if (cost is null || cost.IsEmpty) return null;
        var result = new Dictionary<string, object?>();
        if (cost.Input is not null) result["input"] = cost.Input;
        if (cost.Output is not null) result["output"] = cost.Output;
        if (cost.CacheRead is not null) result[cacheReadKey] = cost.CacheRead;
        if (cost.CacheWrite is not null) result[cacheWriteKey] = cost.CacheWrite;
        return result.Count == 0 ? null : result;
    }

    public static Dictionary<string, object?> ToZcodeModel(ProxyModelConfig model)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = model.GetId(),
            ["limit"] = new Dictionary<string, object?>
            {
                ["context"] = model.MaxContextLength ?? DefaultContextWindow,
                ["output"] = ZcodeOutputLimit
            },
            ["modalities"] = new Dictionary<string, object?>
            {
                ["input"] = NormalizeModalities(model.InputModalities, isInput: true),
                ["output"] = NormalizeModalities(model.OutputModalities, isInput: false)
            }
        };
        var defaultVariant = GetDefaultVariant(model.ThinkingLevels);
        if (defaultVariant is not null)
        {
            result["reasoning"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["variants"] = OrderLevels(model.ThinkingLevels),
                ["defaultVariant"] = defaultVariant
            };
        }
        return result;
    }

    public static Dictionary<string, object?> ToOpencodeModel(ProxyModelConfig model)
    {
        var input = NormalizeModalities(model.InputModalities, isInput: true);
        var result = new Dictionary<string, object?>
        {
            ["name"] = model.GetId(),
            ["attachment"] = input.Contains("image"),
            ["reasoning"] = GetDefaultVariant(model.ThinkingLevels) is not null,
            ["limit"] = new Dictionary<string, object?>
            {
                ["context"] = model.MaxContextLength ?? DefaultContextWindow,
                ["output"] = OpencodeOutputLimit
            },
            ["modalities"] = new Dictionary<string, object?>
            {
                ["input"] = input,
                ["output"] = NormalizeModalities(model.OutputModalities, isInput: false)
            }
        };
        var variants = new Dictionary<string, object?>();
        foreach (var level in OrderLevels(model.ThinkingLevels))
        {
            if (level == "off") continue;
            variants[level] = new Dictionary<string, object?> { ["reasoningEffort"] = level };
        }
        if (variants.Count > 0) result["variants"] = variants;
        var cost = BuildCost(model, "cache_read", "cache_write");
        if (cost is not null) result["cost"] = cost;
        return result;
    }

    public static Dictionary<string, object?> ToDshModel(ProxyModelConfig model)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = model.GetId(),
            ["name"] = model.GetId(),
            ["contextWindow"] = model.MaxContextLength ?? DefaultContextWindow,
            ["maxTokens"] = DshMaxTokens,
            // dsh 的模态枚举只有 text/image，其他（video 等）会让整个 llm-pi-ai 段校验失败
            ["input"] = DshModalities(model.InputModalities)
        };
        var efforts = new Dictionary<string, object?>();
        foreach (var level in OrderLevels(model.ThinkingLevels))
        {
            if (level == "off") continue;
            efforts[level] = level;
        }
        if (efforts.Count > 0) result["reasoningEfforts"] = efforts;
        var cost = BuildCost(model, "cacheRead", "cacheWrite");
        if (cost is not null) result["cost"] = cost;
        return result;
    }

    private static List<string> DshModalities(IReadOnlyList<string> configured)
    {
        var normalized = NormalizeModalities(configured, isInput: true);
        return [.. normalized.Where(modality => modality is "text" or "image")];
    }
}
