using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Infrastructure.LocalUsage;
using ZstdSharp;

namespace OhMyPc.IntegrationTests;

public sealed class DshUsageCollectorTests : IDisposable
{
    private readonly string _sessionsRoot = Path.Combine(Path.GetTempPath(), $"oh-my-pc-dsh-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_sessionsRoot, "settings.yaml");

    public DshUsageCollectorTests() => Directory.CreateDirectory(_sessionsRoot);

    [Fact]
    public void Parser_ReadsMultipleFramesAndSkipsForkSeed()
    {
        var date = new DateOnly(2026, 8, 12);
        var time = UnixMilliseconds(date);
        var session = CompressFrames(
            Header(seedLength: 1),
            Lines(
                Assistant(0, time, "parent-provider", "parent-model", 100, 20, 30, 4, 7),
                RequestHeader(1, "header-provider", "header-model"),
                Assistant(2, time, "message-provider", "message-model", 10, 20, 30, 4, 7)),
            Lines(
                RequestContext(3, "context-provider", "context-model"),
                AssistantWithoutSource(4, time, 5, 6, 8, 9, 11),
                AssistantWithoutUsage(5, time)));

        var rows = DshUsageCollector.ParseSession(
            session,
            fullHistory: true,
            date,
            "test-device",
            DateTimeOffset.UtcNow);

        Assert.Equal(2, rows.Count);
        var sourced = Assert.Single(rows, row => row.Provider == "message-provider");
        Assert.Equal("message-model", sourced.Model);
        Assert.Equal(64, sourced.TotalTokens);
        Assert.Equal(7, sourced.ReasoningTokens);
        Assert.Equal(1, sourced.MessageCount);
        Assert.Equal(0m, sourced.CostUsd);
        var fallback = Assert.Single(rows, row => row.Provider == "context-provider");
        Assert.Equal("context-model", fallback.Model);
        Assert.Equal(28, fallback.TotalTokens);
        Assert.Equal(11, fallback.ReasoningTokens);
        Assert.DoesNotContain(rows, row => row.Provider == "parent-provider");
    }

    [Fact]
    public void Parser_TodayModeUsesEventLocalDate()
    {
        var today = new DateOnly(2026, 8, 12);
        var session = CompressFrames(
            Header(),
            Lines(
                Assistant(0, UnixMilliseconds(today.AddDays(-1)), "input-im", "gpt-5.6-sol", 100, 20),
                Assistant(1, UnixMilliseconds(today), "input-im", "gpt-5.6-sol", 10, 5)));

        var todayRows = DshUsageCollector.ParseSession(
            session,
            fullHistory: false,
            today,
            "test-device",
            DateTimeOffset.UtcNow);
        var historyRows = DshUsageCollector.ParseSession(
            session,
            fullHistory: true,
            today,
            "test-device",
            DateTimeOffset.UtcNow);

        var todayRow = Assert.Single(todayRows);
        Assert.Equal(today, todayRow.Date);
        Assert.Equal(15, todayRow.TotalTokens);
        Assert.Equal(2, historyRows.Count);
    }

    [Fact]
    public async Task Collector_IsStableAcrossRefreshesAndSkipsUnreadableSessions()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var validDirectory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "project", "valid"));
        var invalidDirectory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "project", "invalid"));
        var validPath = Path.Combine(validDirectory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            validPath,
            CompressFrames(
                Header(),
                Assistant(0, UnixMilliseconds(date), "input-im", "gpt-5.6-sol", 10, 5, 20, 2, 3) + "\n"));
        await File.WriteAllBytesAsync(
            Path.Combine(invalidDirectory.FullName, "session.jsonl.zstd"),
            [1, 2, 3, 4]);
        var collector = new DshUsageCollector(_sessionsRoot, SettingsPath, NullLogger<DshUsageCollector>.Instance);

        var first = await collector.CollectAsync(fullHistory: true);
        using var lockedSession = new FileStream(validPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var second = await collector.CollectAsync(fullHistory: true);

        var firstRow = Assert.Single(first);
        var secondRow = Assert.Single(second);
        Assert.Equal(37, firstRow.TotalTokens);
        Assert.Equal(37, secondRow.TotalTokens);
        Assert.Equal(3, secondRow.ReasoningTokens);
        Assert.Equal(1, secondRow.MessageCount);
    }

    [Fact]
    public async Task CollectorReadStream_AllowsDshToAppendSession()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var directory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "shared-read"));
        var path = Path.Combine(directory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "model", 10, 5) + "\n"));

        using var reader = DshUsageCollector.OpenSessionReadStream(path);
        var originalLength = reader.Length;
        await using (var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4_096,
            FileOptions.Asynchronous))
        {
            writer.Seek(0, SeekOrigin.End);
            await writer.WriteAsync(CompressFrames(
                Assistant(1, UnixMilliseconds(date), "input-im", "model", 20, 10) + "\n"));
            await writer.FlushAsync();
        }

        Assert.True(reader.CanRead);
        Assert.True(new FileInfo(path).Length > originalLength);
    }

    [Fact]
    public async Task Collector_RejectsTransientlyUnreadableSessionWithoutCache()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var directory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "locked"));
        var path = Path.Combine(directory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "model", 10, 5) + "\n"));
        using var lockedSession = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var collector = new DshUsageCollector(_sessionsRoot, SettingsPath, NullLogger<DshUsageCollector>.Instance);

        await Assert.ThrowsAsync<IOException>(() => collector.CollectAsync(fullHistory: true));
    }

    [Fact]
    public async Task Collector_CalculatesRequestWideTieredCostsFromSettings()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "priced"));
        await File.WriteAllTextAsync(SettingsPath, """
            llm-pi-ai:
              providers:
                input-im:
                  models:
                    - id: priced-model
                      cost:
                        input: 1
                        output: 2
                        cacheRead: 3
                        cacheWrite: 4
                        tiers:
                          - inputTokensAbove: 200
                            input: 10
                            output: 20
                            cacheRead: 30
                            cacheWrite: 40
                          - inputTokensAbove: 300
                            input: 100
                            output: 200
                            cacheRead: 300
                            cacheWrite: 400
            """);
        await File.WriteAllBytesAsync(
            Path.Combine(sessionDirectory.FullName, "session.jsonl.zstd"),
            CompressFrames(
                Header(),
                Lines(
                    Assistant(0, UnixMilliseconds(date), "input-im", "priced-model", 100, 10, 50, 50),
                    Assistant(1, UnixMilliseconds(date), "input-im", "priced-model", 100, 10, 100, 1),
                    Assistant(2, UnixMilliseconds(date), "input-im", "priced-model", 301, 0))));
        var collector = new DshUsageCollector(_sessionsRoot, SettingsPath, NullLogger<DshUsageCollector>.Instance);

        var row = Assert.Single(await collector.CollectAsync(fullHistory: true));

        Assert.Equal(722, row.TotalTokens);
        Assert.Equal(3, row.MessageCount);
        Assert.Equal(0.034810m, row.CostUsd);
    }

    [Fact]
    public async Task Collector_ReparsesChangedSessionsAndRemovesDeletedContributions()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var directory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "changing"));
        var path = Path.Combine(directory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "model", 10, 5) + "\n"));
        var collector = new DshUsageCollector(_sessionsRoot, SettingsPath, NullLogger<DshUsageCollector>.Instance);

        var first = Assert.Single(await collector.CollectAsync(fullHistory: true));
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "model", 2_000, 500) + "\n"));
        var changed = Assert.Single(await collector.CollectAsync(fullHistory: true));
        File.Delete(path);
        var deleted = await collector.CollectAsync(fullHistory: true);

        Assert.Equal(15, first.TotalTokens);
        Assert.Equal(2_500, changed.TotalTokens);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task Collector_RecalculatesCachedSessionsWhenModelCostsChange()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var directory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "cost-change"));
        var path = Path.Combine(directory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "priced-model", 1_000_000, 0) + "\n"));
        await WriteSimpleCostSettingsAsync(inputCost: 1);
        var collector = new DshUsageCollector(_sessionsRoot, SettingsPath, NullLogger<DshUsageCollector>.Instance);

        var first = Assert.Single(await collector.CollectAsync(fullHistory: true));
        await WriteSimpleCostSettingsAsync(inputCost: 2);
        File.SetLastWriteTimeUtc(SettingsPath, DateTime.UtcNow.AddSeconds(1));
        var changed = Assert.Single(await collector.CollectAsync(fullHistory: true));

        Assert.Equal(1m, first.CostUsd);
        Assert.Equal(2m, changed.CostUsd);
    }

    private Task WriteSimpleCostSettingsAsync(int inputCost) => File.WriteAllTextAsync(SettingsPath, $$"""
        llm-pi-ai:
          providers:
            input-im:
              models:
                - id: priced-model
                  cost:
                    input: {{inputCost}}
                    output: 0
                    cacheRead: 0
                    cacheWrite: 0
        """);

    public void Dispose() => Directory.Delete(_sessionsRoot, recursive: true);

    private static byte[] CompressFrames(params string[] plaintextFrames)
    {
        using var output = new MemoryStream();
        foreach (var plaintext in plaintextFrames)
        {
            using var compressor = new Compressor(3);
            output.Write(compressor.Wrap(Encoding.UTF8.GetBytes(plaintext)));
        }
        return output.ToArray();
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    private static string Header(int? seedLength = null)
    {
        var header = new Dictionary<string, object>
        {
            ["type"] = "session",
            ["version"] = 0,
            ["id"] = "test-session",
            ["createdAt"] = 0,
            ["delegationDepth"] = 0
        };
        if (seedLength is not null) header["seedLength"] = seedLength.Value;
        return JsonSerializer.Serialize(header) + "\n";
    }

    private static string RequestHeader(long seq, string provider, string model) => JsonSerializer.Serialize(new
    {
        type = "request/header",
        seq,
        time = 0,
        data = new
        {
            header = new { config = new { provider, model } },
            reason = "initial"
        }
    });

    private static string RequestContext(long seq, string provider, string model) => JsonSerializer.Serialize(new
    {
        type = "request/context",
        seq,
        time = 0,
        data = new { provider, model }
    });

    private static string Assistant(
        long seq,
        long time,
        string provider,
        string model,
        long input,
        long output,
        long cacheRead = 0,
        long cacheWrite = 0,
        long reasoning = 0) => JsonSerializer.Serialize(new
    {
        type = "assistant/message",
        seq,
        time,
        data = new
        {
            turn = 1,
            step = 1,
            message = new
            {
                role = "assistant",
                content = Array.Empty<object>(),
                source = new { kind = "model", provider, model }
            },
            usage = new
            {
                inputTokens = input,
                outputTokens = output,
                cacheReadTokens = cacheRead,
                cacheWriteTokens = cacheWrite,
                reasoningTokens = reasoning
            }
        }
    });

    private static string AssistantWithoutSource(
        long seq,
        long time,
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long reasoning) => JsonSerializer.Serialize(new
    {
        type = "assistant/message",
        seq,
        time,
        data = new
        {
            turn = 1,
            step = 1,
            message = new { role = "assistant", content = Array.Empty<object>() },
            usage = new
            {
                inputTokens = input,
                outputTokens = output,
                cacheReadTokens = cacheRead,
                cacheWriteTokens = cacheWrite,
                reasoningTokens = reasoning
            }
        }
    });

    private static string AssistantWithoutUsage(long seq, long time) => JsonSerializer.Serialize(new
    {
        type = "assistant/message",
        seq,
        time,
        data = new
        {
            turn = 1,
            step = 1,
            message = new { role = "assistant", content = Array.Empty<object>() }
        }
    });

    private static long UnixMilliseconds(DateOnly date)
    {
        var local = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }
}
