using OhMyPc.Core;
using OhMyPc.Core.Domain;
using Xunit;

namespace OhMyPc.Core.Tests;

public sealed class ProxyMappersTests
{
    [Fact]
    public void OrderLevels_SortsByStrengthAndDropsUnknown()
    {
        var ordered = ProxyMappers.OrderLevels(["max", "bogus", "low", "xhigh"]);
        Assert.Equal(["low", "xhigh", "max"], ordered);
    }

    [Fact]
    public void GetDefaultVariant_PicksHighest()
    {
        Assert.Equal("max", ProxyMappers.GetDefaultVariant(["low", "max", "medium"]));
        Assert.Equal("high", ProxyMappers.GetDefaultVariant(["high", "low"]));
    }

    [Fact]
    public void GetDefaultVariant_OffOnlyOrEmpty_ReturnsNull()
    {
        Assert.Null(ProxyMappers.GetDefaultVariant(["off"]));
        Assert.Null(ProxyMappers.GetDefaultVariant([]));
    }

    [Fact]
    public void ToZcodeModel_UsesAliasAsIdAndDefaults()
    {
        var model = new ProxyModelConfig { Name = "glm-5.3", Alias = "GLM-5.3", ThinkingLevels = ["low", "max"] };
        var mapped = ProxyMappers.ToZcodeModel(model);

        Assert.Equal("GLM-5.3", mapped["name"]);
        var reasoning = (Dictionary<string, object?>)mapped["reasoning"]!;
        Assert.Equal(true, reasoning["enabled"]);
        Assert.Equal(new[] { "low", "max" }, (IEnumerable<string>)reasoning["variants"]!);
        Assert.Equal("max", reasoning["defaultVariant"]);
        var limit = (Dictionary<string, object?>)mapped["limit"]!;
        Assert.Equal(272000L, limit["context"]);
        Assert.Equal(128000L, limit["output"]);
        var modalities = (Dictionary<string, object?>)mapped["modalities"]!;
        Assert.Equal(["text", "image"], (IEnumerable<string>)modalities["input"]!);
        Assert.Equal(["text"], (IEnumerable<string>)modalities["output"]!);
    }

    [Fact]
    public void ToZcodeModel_WithoutLevels_OmitsReasoning()
    {
        var mapped = ProxyMappers.ToZcodeModel(new ProxyModelConfig { Name = "gpt-5.6-terra" });
        Assert.DoesNotContain("reasoning", mapped.Keys);
    }

    [Fact]
    public void ToOpencodeModel_BuildsReasoningEffortVariants()
    {
        var model = new ProxyModelConfig
        {
            Name = "gpt-5.6-sol",
            ThinkingLevels = ["medium", "xhigh", "max", "off"],
            MaxContextLength = 400000
        };
        var mapped = ProxyMappers.ToOpencodeModel(model);

        Assert.Equal(true, mapped["reasoning"]);
        var limit = (Dictionary<string, object?>)mapped["limit"]!;
        Assert.Equal(400000L, limit["context"]);
        Assert.Equal(131072L, limit["output"]);
        var variants = (Dictionary<string, object?>)mapped["variants"]!;
        Assert.Equal(["max", "medium", "xhigh"], variants.Keys.Order(StringComparer.Ordinal).ToArray());
        var xhigh = (Dictionary<string, object?>)variants["xhigh"]!;
        Assert.Equal("xhigh", xhigh["reasoningEffort"]);
    }

    [Fact]
    public void ToOpencodeModel_NoLevels_ReasoningDisabled()
    {
        var mapped = ProxyMappers.ToOpencodeModel(new ProxyModelConfig { Name = "m" });
        Assert.Equal(false, mapped["reasoning"]);
        Assert.DoesNotContain("variants", mapped.Keys);
    }

    [Fact]
    public void ToDshModel_MapsEffortsAndKeepsExplicitModalities()
    {
        var model = new ProxyModelConfig
        {
            Name = "gpt-5.6-sol",
            ThinkingLevels = ["low", "max"],
            InputModalities = ["text"]
        };
        var mapped = ProxyMappers.ToDshModel(model);

        Assert.Equal("gpt-5.6-sol", mapped["id"]);
        Assert.Equal(128000L, mapped["maxTokens"]);
        Assert.Equal(["text"], (IEnumerable<string>)mapped["input"]!);
        var efforts = (Dictionary<string, object?>)mapped["reasoningEfforts"]!;
        Assert.Equal(2, efforts.Count);
        Assert.Equal("max", efforts["max"]);
    }

    [Fact]
    public void ToOpencodeModel_IncludesCostWithSnakeCaseKeys()
    {
        var model = new ProxyModelConfig
        {
            Name = "gpt-5.6-sol",
            Cost = new ProxyModelCost { Input = 5m, Output = 30m, CacheRead = 0.5m, CacheWrite = 6.25m }
        };
        var mapped = ProxyMappers.ToOpencodeModel(model);

        var cost = (Dictionary<string, object?>)mapped["cost"]!;
        Assert.Equal(5m, cost["input"]);
        Assert.Equal(30m, cost["output"]);
        Assert.Equal(0.5m, cost["cache_read"]);
        Assert.Equal(6.25m, cost["cache_write"]);
    }

    [Fact]
    public void ToDshModel_IncludesCostWithCamelCaseKeysAndOmitsEmptyCost()
    {
        var withCost = ProxyMappers.ToDshModel(new ProxyModelConfig
        {
            Name = "m",
            Cost = new ProxyModelCost { Input = 5m, CacheRead = 0.5m }
        });
        var cost = (Dictionary<string, object?>)withCost["cost"]!;
        Assert.Equal(5m, cost["input"]);
        Assert.Equal(0.5m, cost["cacheRead"]);
        Assert.DoesNotContain("output", cost.Keys);

        var withoutCost = ProxyMappers.ToDshModel(new ProxyModelConfig { Name = "m" });
        Assert.DoesNotContain("cost", withoutCost.Keys);
    }

    [Fact]
    public void ToDshModel_FiltersModalitiesToTextAndImage()
    {
        // dsh 的模态枚举只有 text/image，video/audio 会让整个 llm-pi-ai 段校验失败
        var mapped = ProxyMappers.ToDshModel(new ProxyModelConfig
        {
            Name = "m",
            InputModalities = ["text", "image", "video", "audio"]
        });
        Assert.Equal(["text", "image"], (IEnumerable<string>)mapped["input"]!);

        var defaults = ProxyMappers.ToDshModel(new ProxyModelConfig { Name = "m" });
        Assert.Equal(["text", "image"], (IEnumerable<string>)defaults["input"]!);
    }
}
