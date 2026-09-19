using System.Text.Json.Nodes;
using OhMyPc.Infrastructure.LocalUsage;

namespace OhMyPc.IntegrationTests;

public sealed class WorkbuddyUsageCollectorTests : IDisposable
{
    private readonly string _projectsRoot = Path.Combine(Path.GetTempPath(), $"oh-my-pc-workbuddy-{Guid.NewGuid():N}");

    public WorkbuddyUsageCollectorTests() => Directory.CreateDirectory(_projectsRoot);

    [Fact]
    public async Task ParseSession_AggregatesUsageWithCacheConversion()
    {
        var date = new DateOnly(2026, 9, 18);
        var sessionPath = Path.Combine(_projectsRoot, "proj-a", "session-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var lines = new[]
        {
            // user 消息没有 usage：跳过
            """{"id":"u1","timestamp":1789704564000,"type":"message","role":"user"}""",
            // assistant 回复：input 为含缓存口径（hit 12864 + miss 25264 = 38128），推理含于 output
            UsageLine("message", UnixMs(date, 20), "hy4-preview-f",
                usage: [("input_tokens", 38128L), ("output_tokens", 415L), ("cache_read_input_tokens", 12864L)],
                cacheCreation: 0, thinking: 236),
            // 工具调用请求是独立的一次 API 请求；缓存写入来自 rawUsage.provider 专有字段
            UsageLine("function_call", UnixMs(date, 30), "hy4-preview-f",
                usage: [("input_tokens", 500L), ("output_tokens", 50L), ("cache_read_input_tokens", 100L)],
                cacheCreation: 20, thinking: 10),
            // 畸形行跳过不致失败
            "{not-json"
        };
        await File.WriteAllTextAsync(sessionPath, string.Join('\n', lines));

        var rows = await WorkbuddyUsageCollector.ParseSessionAsync(
            sessionPath, fullHistory: true, date, "test-device", CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("workbuddy", row.Client);
        Assert.Equal("hy4-preview-f", row.Model);
        // 两行聚合一组：净输入 = (38128−12864) + (500−100−20)
        Assert.Equal(25264 + 380, row.InputTokens);
        Assert.Equal(465, row.OutputTokens);
        Assert.Equal(12864 + 100, row.CacheReadTokens);
        Assert.Equal(20, row.CacheWriteTokens);
        Assert.Equal(246, row.ReasoningTokens);
        Assert.Equal(2, row.MessageCount);
    }

    [Fact]
    public async Task ParseSession_TodayModeFiltersByLocalDate()
    {
        var today = new DateOnly(2026, 9, 18);
        var sessionPath = Path.Combine(_projectsRoot, "proj-a", "session-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var lines = new[]
        {
            UsageLine("message", UnixMs(today.AddDays(-1), 10), "m-a",
                usage: [("input_tokens", 10L), ("output_tokens", 1L)], cacheCreation: 0, thinking: 0),
            UsageLine("message", UnixMs(today, 10), "m-a",
                usage: [("input_tokens", 5L), ("output_tokens", 1L)], cacheCreation: 0, thinking: 0)
        };
        await File.WriteAllTextAsync(sessionPath, string.Join('\n', lines));

        var todayRows = await WorkbuddyUsageCollector.ParseSessionAsync(
            sessionPath, fullHistory: false, today, "t", CancellationToken.None);
        var historyRows = await WorkbuddyUsageCollector.ParseSessionAsync(
            sessionPath, fullHistory: true, today, "t", CancellationToken.None);

        var todayRow = Assert.Single(todayRows);
        Assert.Equal(today, todayRow.Date);
        Assert.Equal(2, historyRows.Count);
    }

    private static long UnixMs(DateOnly date, int minutes) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.Parse("08:00"))).AddMinutes(minutes).ToUnixTimeMilliseconds();

    private static string UsageLine(
        string type, long timestamp, string model,
        (string Key, long Value)[] usage, long cacheCreation, long thinking)
    {
        var usageObject = new JsonObject();
        foreach (var (key, value) in usage) usageObject[key] = value;
        var entry = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["timestamp"] = timestamp,
            ["type"] = type,
            ["message"] = new JsonObject { ["usage"] = usageObject },
            ["providerData"] = new JsonObject
            {
                ["model"] = model,
                ["requestModelId"] = model,
                ["rawUsage"] = new JsonObject
                {
                    ["prompt_cache_write_tokens"] = 0,
                    ["cache_creation_input_tokens"] = cacheCreation,
                    ["completion_thinking_tokens"] = thinking
                }
            }
        };
        return entry.ToJsonString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectsRoot)) Directory.Delete(_projectsRoot, recursive: true);
    }
}
