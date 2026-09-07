using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.LocalUsage;
using ZstdSharp;

namespace OhMyPc.IntegrationTests;

public sealed class DshUsageCollectorTests : IDisposable
{
    private readonly string _sessionsRoot = Path.Combine(Path.GetTempPath(), $"oh-my-pc-dsh-{Guid.NewGuid():N}");

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
        // 非空目录才会走「解析结果入缓存」路径，锁文件后第二轮依赖缓存快照
        var collector = CreateCollector(metadata: Metadata(("gpt-5.6-sol", new ProxyModelCost { Input = 1m })));

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
        var collector = CreateCollector();

        await Assert.ThrowsAsync<IOException>(() => collector.CollectAsync(fullHistory: true));
    }

    [Fact]
    public async Task Collector_PricesUsageFromCatalog()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "priced"));
        await File.WriteAllBytesAsync(
            Path.Combine(sessionDirectory.FullName, "session.jsonl.zstd"),
            CompressFrames(
                Header(),
                Lines(
                    Assistant(0, UnixMilliseconds(date), "input-im", "priced-model", 1_000_000, 500_000, 250_000, 0),
                    Assistant(1, UnixMilliseconds(date), "input-im", "unknown-model", 1_000_000, 0))));
        var collector = CreateCollector(metadata: Metadata(
            ("priced-model", new ProxyModelCost { Input = 2m, Output = 4m, CacheRead = 0.5m })));

        var rows = await collector.CollectAsync(fullHistory: true);

        var priced = Assert.Single(rows, row => row.Model == "priced-model");
        Assert.Equal(2m + 4m * 0.5m + 0.5m * 0.25m, priced.CostUsd);
        Assert.Equal(0m, Assert.Single(rows, row => row.Model == "unknown-model").CostUsd);
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
        var collector = CreateCollector();

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
    public async Task Collector_PricesUsageAfterCatalogBecomesAvailable()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var directory = Directory.CreateDirectory(Path.Combine(_sessionsRoot, "cost-change"));
        var path = Path.Combine(directory.FullName, "session.jsonl.zstd");
        await File.WriteAllBytesAsync(
            path,
            CompressFrames(Header(), Assistant(0, UnixMilliseconds(date), "input-im", "priced-model", 1_000_000, 0) + "\n"));
        var provider = new StubMetadataProvider(new Dictionary<string, ModelMetadata>());
        var collector = new DshUsageCollector(_sessionsRoot, provider, NullLogger<DshUsageCollector>.Instance);

        var unpriced = Assert.Single(await collector.CollectAsync(fullHistory: true));
        provider.Metadata = Metadata(("priced-model", new ProxyModelCost { Input = 2m }));
        var priced = Assert.Single(await collector.CollectAsync(fullHistory: true));

        // 首次目录不可用（空目录）时不计费；目录出现后同一会话按牌价补算
        Assert.Equal(0m, unpriced.CostUsd);
        Assert.Equal(2m, priced.CostUsd);
    }

    private static IReadOnlyDictionary<string, ModelMetadata> Metadata(params (string Id, ProxyModelCost Cost)[] entries) =>
        entries.ToDictionary(
            entry => entry.Id,
            entry => new ModelMetadata { Id = entry.Id, Cost = entry.Cost },
            StringComparer.OrdinalIgnoreCase);

    private DshUsageCollector CreateCollector(
        IReadOnlyDictionary<string, ModelMetadata>? metadata = null) =>
        new(_sessionsRoot, new StubMetadataProvider(metadata ?? new Dictionary<string, ModelMetadata>()), NullLogger<DshUsageCollector>.Instance);

    private sealed class StubMetadataProvider(IReadOnlyDictionary<string, ModelMetadata> metadata) : IModelMetadataProvider
    {
        public IReadOnlyDictionary<string, ModelMetadata> Metadata { get; set; } = metadata;

        public Task<IReadOnlyDictionary<string, ModelMetadata>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Metadata);
    }

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
