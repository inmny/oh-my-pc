using System.Text.Json;
using OhMyPc.Infrastructure.LocalUsage;

namespace OhMyPc.IntegrationTests;

public sealed class TokscaleClientTests
{
    [Fact]
    public void TodayParser_UsesCapturedDateAndAggregatesTokenDimensions()
    {
        using var document = JsonDocument.Parse("""
            {
              "entries": [
                {
                  "client": "opencode",
                  "provider": "tken",
                  "model": "gpt-5.6-sol",
                  "input": 10,
                  "output": 20,
                  "cacheRead": 30,
                  "cacheWrite": 4,
                  "reasoning": 7,
                  "messageCount": 2,
                  "cost": 0.12
                },
                {
                  "client": "opencode",
                  "provider": "tken",
                  "model": "gpt-5.6-sol",
                  "input": 5,
                  "output": 6,
                  "cacheRead": 8,
                  "cacheWrite": 9,
                  "reasoning": 11,
                  "messageCount": 3,
                  "cost": 0.23
                }
              ]
            }
            """);
        var collectionDate = new DateOnly(2026, 8, 12);

        var rows = TokscaleClient.ParseToday(document.RootElement, collectionDate);

        var row = Assert.Single(rows);
        Assert.Equal(collectionDate, row.Date);
        Assert.Equal(15, row.InputTokens);
        Assert.Equal(26, row.OutputTokens);
        Assert.Equal(38, row.CacheReadTokens);
        Assert.Equal(13, row.CacheWriteTokens);
        Assert.Equal(92, row.TotalTokens);
        Assert.Equal(18, row.ReasoningTokens);
        Assert.Equal(5, row.MessageCount);
        Assert.Equal(0.35m, row.CostUsd);
    }

    [Fact]
    public void GraphParser_UsesContributionDateAndPreservesActivityRows()
    {
        using var document = JsonDocument.Parse("""
            {
              "contributions": [
                {
                  "date": "2026-08-11",
                  "activeTimeMs": 1234,
                  "clients": [
                    {
                      "client": "opencode",
                      "providerId": "tken",
                      "modelId": "gpt-5.6-sol",
                      "messages": 4,
                      "cost": 0.5,
                      "tokens": {
                        "input": 12,
                        "output": 8,
                        "cacheRead": 3,
                        "cacheWrite": 2,
                        "reasoning": 1
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var rows = TokscaleClient.ParseGraph(document.RootElement);

        Assert.Equal(2, rows.Count);
        var usage = Assert.Single(rows, row => row.Client == "opencode");
        Assert.Equal(new DateOnly(2026, 8, 11), usage.Date);
        Assert.Equal(25, usage.TotalTokens);
        var activity = Assert.Single(rows, row => row.Client == "_activity");
        Assert.Equal(new DateOnly(2026, 8, 11), activity.Date);
        Assert.Equal(1234, activity.ActiveTimeMs);
    }
}
