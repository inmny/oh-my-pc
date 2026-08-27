using OhMyPc.Core;
using Xunit;

namespace OhMyPc.Core.Tests;

public sealed class ModelMetadataParserTests
{
    private const string SampleJson =
        """
        {
          "provider-a": {
            "models": {
              "gpt-test": {
                "limit": { "context": 272000 },
                "modalities": { "input": ["text", "image", "pdf"], "output": ["text"] },
                "reasoning_options": [ { "type": "effort", "values": ["none", "low", "max"] } ],
                "cost": { "input": 5, "output": 30, "cache_read": 0.5, "cache_write": 6.25 }
              },
              "no-metadata": { "name": "No Metadata Model" },
              "org/model-x": {
                "limit": { "context": 128000 },
                "modalities": { "input": ["text"], "output": ["text"] },
                "reasoning_options": [ { "type": "toggle" } ],
                "cost": { "input": 1 }
              }
            }
          },
          "provider-b": {
            "models": {
              "gpt-test": { "limit": { "context": 272000 } },
              "duplicate-with-cost": { "cost": { "input": 2, "output": 8 } }
            }
          },
          "provider-c": {
            "models": {
              "duplicate-with-cost": { "limit": { "context": 99000 } }
            }
          }
        }
        """;

    [Fact]
    public void Parse_MapsFieldsAndNormalizes()
    {
        var lookup = ModelMetadataParser.Parse(SampleJson);

        var model = lookup["gpt-test"];
        Assert.Equal(272000, model.ContextWindow);
        Assert.Equal(["text", "image"], model.InputModalities);
        Assert.Equal(["text"], model.OutputModalities);
        // none→off，未知档位被过滤，按强度排序
        Assert.Equal(["off", "low", "max"], model.ThinkingLevels);
        Assert.Equal(5m, model.Cost.Input);
        Assert.Equal(30m, model.Cost.Output);
        Assert.Equal(0.5m, model.Cost.CacheRead);
        Assert.Equal(6.25m, model.Cost.CacheWrite);
    }

    [Fact]
    public void Parse_RegistersSuffixIdForPrefixedModels()
    {
        var lookup = ModelMetadataParser.Parse(SampleJson);

        Assert.Contains("org/model-x", lookup);
        var suffix = lookup["model-x"];
        Assert.Equal(128000, suffix.ContextWindow);
        // toggle 型思考选项不产生档位
        Assert.Empty(suffix.ThinkingLevels);
    }

    [Fact]
    public void Parse_MergesDuplicatesPreferringEntriesWithCost()
    {
        var lookup = ModelMetadataParser.Parse(SampleJson);

        // provider-c 的条目无费用，provider-b 带费用的胜出（即使后出现）
        Assert.Equal(2m, lookup["duplicate-with-cost"].Cost.Input);
    }

    [Fact]
    public void Parse_ToleratesEntriesWithoutMetadata()
    {
        var lookup = ModelMetadataParser.Parse(SampleJson);

        Assert.Contains("no-metadata", lookup);
        Assert.Null(lookup["no-metadata"].ContextWindow);
        Assert.True(lookup["no-metadata"].Cost.IsEmpty);
    }
}
