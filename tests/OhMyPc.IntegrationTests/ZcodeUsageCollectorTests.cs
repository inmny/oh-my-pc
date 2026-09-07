using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.LocalUsage;

namespace OhMyPc.IntegrationTests;

public sealed class ZcodeUsageCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"oh-my-pc-zcode-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_root, "db.sqlite");

    public void SetUp() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Collector_AggregatesCompletedRowsByProviderAndModel()
    {
        SetUp();
        var date = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(
            Row(date, "builtin:bigmodel-coding-plan", "GLM-5.3", input: 6000, output: 200, cacheRead: 5000, cacheWrite: 300, reasoning: 7),
            Row(date, "builtin:bigmodel-coding-plan", "GLM-5.3", input: 2000, output: 100),
            Row(date, "builtin:bigmodel-coding-plan", "GLM-5.3-Flash", input: 40, output: 10),
            Row(date, "builtin:bigmodel-coding-plan", "GLM-5.3", input: 999, output: 999, status: "cancelled"),
            Row(date, "builtin:bigmodel-coding-plan", "GLM-5.3", input: 888, output: 888, status: "error"));
        var collector = CreateCollector();

        var rows = await collector.CollectAsync(fullHistory: true);

        var main = Assert.Single(rows, row => row.Model == "GLM-5.3");
        Assert.Equal("zcode", main.Client);
        Assert.Equal("builtin:bigmodel-coding-plan", main.Provider);
        // input 为含缓存口径：6000-5000-300=700 为未命中缓存的新增输入
        Assert.Equal(700 + 2000, main.InputTokens);
        Assert.Equal(300, main.OutputTokens);
        Assert.Equal(5000, main.CacheReadTokens);
        Assert.Equal(300, main.CacheWriteTokens);
        Assert.Equal(8300, main.TotalTokens);
        Assert.Equal(7, main.ReasoningTokens);
        Assert.Equal(2, main.MessageCount);
        Assert.Equal(0m, main.CostUsd);
        Assert.Single(rows, row => row.Model == "GLM-5.3-Flash");
    }

    [Fact]
    public async Task Collector_ConvertsInclusiveInputToUncachedInput()
    {
        SetUp();
        var date = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(
            Row(date, "builtin-plan", "GLM-5.3", input: 10_000, output: 300, cacheRead: 9_000, cacheWrite: 500),
            // 防御：上游数据异常（缓存大于输入）时 InputTokens 钳制为 0 而不是负数
            Row(date, "builtin-plan", "GLM-5.3-Flash", input: 100, output: 40, cacheRead: 200));
        var collector = CreateCollector();

        var rows = await collector.CollectAsync(fullHistory: true);

        var glm = Assert.Single(rows, row => row.Model == "GLM-5.3");
        Assert.Equal(500, glm.InputTokens);
        Assert.Equal(10_300, glm.TotalTokens);
        var flash = Assert.Single(rows, row => row.Model == "GLM-5.3-Flash");
        Assert.Equal(0, flash.InputTokens);
        Assert.Equal(240, flash.TotalTokens);
    }

    [Fact]
    public async Task Collector_TodayModeFiltersOtherDays()
    {
        SetUp();
        var today = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(
            Row(today.AddDays(-2), "cpa-gw", "gpt-5.6-terra", input: 100, output: 20),
            Row(today, "cpa-gw", "gpt-5.6-terra", input: 10, output: 5));
        var collector = CreateCollector();

        var todayRow = Assert.Single(await collector.CollectAsync(fullHistory: false));
        var historyRows = await collector.CollectAsync(fullHistory: true);

        Assert.Equal(15, todayRow.TotalTokens);
        Assert.Equal(2, historyRows.Count);
    }

    [Fact]
    public async Task Collector_ValuesAllProvidersAtCatalogRates()
    {
        SetUp();
        var date = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(
            Row(date, "cpa-gw", "GPT-5.6-Terra", input: 1_000_000, output: 500_000),
            Row(date, "builtin-plan", "gpt-5.6-terra-high", input: 1_000_000, output: 500_000));
        var collector = CreateCollector(
            metadata: Metadata(("gpt-5.6-terra", new ProxyModelCost { Input = 3m, Output = 2m })));

        var rows = await collector.CollectAsync(fullHistory: true);

        // 别名（经 CPA 别名表）与变体后缀名都归一到目录同一费率
        Assert.Equal(3m + 2m * 0.5m, Assert.Single(rows, row => row.Provider == "cpa-gw").CostUsd);
        Assert.Equal(3m + 2m * 0.5m, Assert.Single(rows, row => row.Provider == "builtin-plan").CostUsd);
    }

    [Fact]
    public async Task Collector_ValuesUnknownModelsAtZero()
    {
        SetUp();
        var date = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(
            Row(date, "offpeak-idle-plan", "GLM-5.3", input: 6_000_000, output: 1_000_000, cacheRead: 4_000_000),
            Row(date, "builtin-plan", "Unknown-Model", input: 1_000_000, output: 0));
        var collector = CreateCollector(
            snapshot: AliasSnapshot("GLM-5.3", "glm-5.3"),
            metadata: Metadata(("glm-5.3", new ProxyModelCost { Input = 1m, Output = 2m, CacheRead = 0.25m })));

        var rows = await collector.CollectAsync(fullHistory: true);

        // 别名 GLM-5.3 → glm-5.3 命中目录费率；未收录模型不计费
        Assert.Equal(2m * 1m + 1m * 2m + 4m * 0.25m, Assert.Single(rows, row => row.Model == "GLM-5.3").CostUsd);
        Assert.Equal(0m, Assert.Single(rows, row => row.Model == "Unknown-Model").CostUsd);
    }

    [Fact]
    public async Task Collector_PicksUpNewRowsAfterDatabaseChanges()
    {
        SetUp();
        var date = DateOnly.FromDateTime(DateTime.Now);
        CreateDatabase(Row(date, "cpa-gw", "GLM-5.3", input: 10, output: 5));
        var collector = CreateCollector();

        var first = Assert.Single(await collector.CollectAsync(fullHistory: true));
        InsertRow(Row(date, "cpa-gw", "GLM-5.3", input: 2000, output: 500));
        Touch(DbPath);
        var changed = Assert.Single(await collector.CollectAsync(fullHistory: true));

        Assert.Equal(15, first.TotalTokens);
        Assert.Equal(2515, changed.TotalTokens);
        Assert.Equal(2, changed.MessageCount);
    }

    [Fact]
    public async Task Collector_ReturnsEmptyWhenDatabaseMissing()
    {
        SetUp();
        var collector = new ZcodeUsageCollector(
            Path.Combine(_root, "missing.db"),
            new StubProxyStore(new ProxyConfigSnapshot()),
            new StubMetadataProvider(new Dictionary<string, ModelMetadata>()),
            NullLogger<ZcodeUsageCollector>.Instance);

        Assert.Empty(await collector.CollectAsync(fullHistory: true));
    }

    private ZcodeUsageCollector CreateCollector(
        ProxyConfigSnapshot? snapshot = null,
        IReadOnlyDictionary<string, ModelMetadata>? metadata = null) =>
        new(
            DbPath,
            new StubProxyStore(snapshot ?? new ProxyConfigSnapshot()),
            new StubMetadataProvider(metadata ?? new Dictionary<string, ModelMetadata>()),
            NullLogger<ZcodeUsageCollector>.Instance);

    private static IReadOnlyDictionary<string, ModelMetadata> Metadata(params (string Id, ProxyModelCost Cost)[] entries) =>
        entries.ToDictionary(
            entry => entry.Id,
            entry => new ModelMetadata { Id = entry.Id, Cost = entry.Cost },
            StringComparer.OrdinalIgnoreCase);

    private static ProxyConfigSnapshot AliasSnapshot(string alias, string name) => new()
    {
        Providers =
        [
            new ProxyProviderConfig
            {
                Kind = ProxyProviderKind.Codex,
                ApiKey = "k",
                BaseUrl = "https://relay.example",
                Models = [new ProxyModelConfig { Name = name, Alias = alias }]
            }
        ]
    };

    private void CreateDatabase(params (string Provider, string Model, long CompletedAt, long Input, long Output, long CacheRead, long CacheWrite, long Reasoning, string Status)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE model_usage (
                    id INTEGER PRIMARY KEY,
                    provider_id TEXT, model_id TEXT, status TEXT, completed_at INTEGER,
                    input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
                    cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER)
                """;
            create.ExecuteNonQuery();
        }
        foreach (var row in rows) InsertRow(row, connection);
    }

    private void InsertRow(
        (string Provider, string Model, long CompletedAt, long Input, long Output, long CacheRead, long CacheWrite, long Reasoning, string Status) row,
        SqliteConnection? connection = null)
    {
        var owns = connection is null;
        connection ??= new SqliteConnection($"Data Source={DbPath};Pooling=False");
        if (owns) connection.Open();
        try
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO model_usage (provider_id, model_id, status, completed_at,
                    input_tokens, output_tokens, reasoning_tokens, cache_creation_input_tokens, cache_read_input_tokens)
                VALUES ($provider, $model, $status, $completedAt, $input, $output, $reasoning, $cacheWrite, $cacheRead)
                """;
            insert.Parameters.AddWithValue("$provider", row.Provider);
            insert.Parameters.AddWithValue("$model", row.Model);
            insert.Parameters.AddWithValue("$status", row.Status);
            insert.Parameters.AddWithValue("$completedAt", row.CompletedAt);
            insert.Parameters.AddWithValue("$input", row.Input);
            insert.Parameters.AddWithValue("$output", row.Output);
            insert.Parameters.AddWithValue("$reasoning", row.Reasoning);
            insert.Parameters.AddWithValue("$cacheWrite", row.CacheWrite);
            insert.Parameters.AddWithValue("$cacheRead", row.CacheRead);
            insert.ExecuteNonQuery();
        }
        finally
        {
            if (owns) connection.Dispose();
        }
    }

    private static (string, string, long, long, long, long, long, long, string) Row(
        DateOnly date,
        string provider,
        string model,
        long input,
        long output,
        long cacheRead = 0,
        long cacheWrite = 0,
        long reasoning = 0,
        string status = "completed")
    {
        var local = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        return (provider, model, new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds(), input, output, cacheRead, cacheWrite, reasoning, status);
    }

    private static void Touch(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

    private sealed class StubProxyStore(ProxyConfigSnapshot? snapshot) : IProxyConfigStore
    {
        public Task<ProxyConfigSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            snapshot is not null
                ? Task.FromResult(snapshot)
                : throw new FileNotFoundException("未找到 CLIProxyAPI 配置文件。");

        public Task SaveAsync(ProxyConfigSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> EnsureConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class StubMetadataProvider(IReadOnlyDictionary<string, ModelMetadata> metadata) : IModelMetadataProvider
    {
        public Task<IReadOnlyDictionary<string, ModelMetadata>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(metadata);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
