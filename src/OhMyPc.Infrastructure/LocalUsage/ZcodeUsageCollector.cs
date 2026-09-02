using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.CliProxy;
using YamlDotNet.Core;

namespace OhMyPc.Infrastructure.LocalUsage;

/// <summary>
/// 读取 zcode 用量数据库（~/.zcode/cli/db/db.sqlite 的 model_usage 表）统计用量；
/// zcode 应用自身展示的累计用量即来自此表（rollout 目录的 model-io 日志只覆盖个别会话，不能作数据源）。
/// 数据库为 WAL 模式且被 zcode 持有：先复制 db 与 wal 到临时文件再查询，避免与写入方冲突。
/// 计费优先级：走 CLIProxyAPI 网关的 provider（从 zcode config.json 识别）用 CPA 配置费率；
/// 其余（builtin:*、offpeak-idle-plan 等订阅 provider）按 models.dev 牌价折算等值成本。
/// </summary>
public sealed class ZcodeUsageCollector : ILocalUsageCollector
{
    private readonly string _dbPath;
    private readonly IReadOnlyList<string> _zcodeConfigPaths;
    private readonly IProxyConfigStore _proxyStore;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<ZcodeUsageCollector> _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private DatabaseStamp? _cachedStamp;
    private IReadOnlyList<UsageObservation>? _cachedObservations;

    public ZcodeUsageCollector(
        LocalToolDetector detector,
        IProxyConfigStore proxyStore,
        IModelMetadataProvider metadataProvider,
        ILogger<ZcodeUsageCollector> logger)
        : this(
            detector.ZcodeDatabasePath,
            [ProxyClientPaths.ZcodeDesktopConfig, ProxyClientPaths.ZcodeCliConfig],
            proxyStore,
            metadataProvider,
            logger)
    {
    }

    internal ZcodeUsageCollector(
        string dbPath,
        IReadOnlyList<string> zcodeConfigPaths,
        IProxyConfigStore proxyStore,
        IModelMetadataProvider metadataProvider,
        ILogger<ZcodeUsageCollector> logger)
    {
        _dbPath = dbPath;
        _zcodeConfigPaths = zcodeConfigPaths;
        _proxyStore = proxyStore;
        _metadataProvider = metadataProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UsageObservation>> CollectAsync(
        bool fullHistory,
        CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CollectCoreAsync(fullHistory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<IReadOnlyList<UsageObservation>> CollectCoreAsync(
        bool fullHistory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_dbPath)) return [];

        var stopwatch = Stopwatch.StartNew();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stamp = GetDatabaseStamp();
        if (stamp is null) return [];

        IReadOnlyList<UsageObservation>? observations = null;
        if (_cachedObservations is not null && _cachedStamp == stamp)
        {
            observations = _cachedObservations;
        }
        else
        {
            try
            {
                observations = ReadDatabase(today, LocalUsageDevice.Id());
                _cachedObservations = observations;
                _cachedStamp = stamp;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_cachedObservations is not null)
                {
                    _logger.LogWarning(exception, "无法读取 zcode 用量数据库，继续使用上次有效快照");
                    observations = _cachedObservations;
                }
                else if (exception is IOException or UnauthorizedAccessException or SqliteException)
                {
                    throw new IOException("zcode 用量数据库暂时不可读。", exception);
                }
                else
                {
                    throw;
                }
            }
        }

        var costs = await ReadCostTableAsync(cancellationToken).ConfigureAwait(false);
        ApplyCosts(observations, costs);

        stopwatch.Stop();
        _logger.LogDebug(
            "zcode 用量采集完成：{Count} 条观测（缓存 {Cached}），耗时 {ElapsedMs} ms",
            observations.Count,
            _cachedStamp == stamp,
            stopwatch.ElapsedMilliseconds);
        return fullHistory
            ? observations
            : observations.Where(item => item.Date == today).ToList();
    }

    private IReadOnlyList<UsageObservation> ReadDatabase(DateOnly today, string deviceId)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-zcode-{Guid.NewGuid():N}.db");
        try
        {
            // 只复制 db 与 wal（wal 帧带校验和，复制瞬间的残帧会被 SQLite 安全丢弃）；
            // shm 是共享内存索引，复制反而会失配，SQLite 会自行重建
            File.Copy(_dbPath, tempPath, overwrite: true);
            var walPath = _dbPath + "-wal";
            if (File.Exists(walPath)) File.Copy(walPath, tempPath + "-wal", overwrite: true);
            return QueryUsage(tempPath, today, deviceId);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(tempPath + "-wal");
            TryDelete(tempPath + "-shm");
        }
    }

    private static IReadOnlyList<UsageObservation> QueryUsage(string dbPath, DateOnly today, string deviceId)
    {
        var aggregate = new Dictionary<(DateOnly Date, string Provider, string Model), UsageObservation>();
        // Pooling=False：连接池会在关闭后仍持有文件句柄，导致临时副本无法删除
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_id, model_id, completed_at, input_tokens, output_tokens,
                   reasoning_tokens, cache_creation_input_tokens, cache_read_input_tokens
            FROM model_usage
            WHERE status = 'completed' AND completed_at IS NOT NULL
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var provider = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
            var model = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
            var completedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            var date = DateOnly.FromDateTime(completedAt.LocalDateTime);
            var key = (date, provider, model);
            if (!aggregate.TryGetValue(key, out var target))
            {
                target = new UsageObservation
                {
                    Date = date,
                    DeviceId = deviceId,
                    Client = "zcode",
                    Provider = provider,
                    Model = model
                };
                aggregate[key] = target;
            }

            // zcode 的 input_tokens 为 OpenAI 口径：已包含缓存命中的部分（实证：逐行
            // provider_total == input+output，且不存在 input < cacheRead 的行）。
            // 换算为应用统一的不含缓存口径：InputTokens 只记未命中缓存的新增输入。
            var cacheWrite = Integer(reader, 6);
            var cacheRead = Integer(reader, 7);
            target.InputTokens += Math.Max(0, Integer(reader, 3) - cacheRead - cacheWrite);
            target.OutputTokens += Integer(reader, 4);
            target.ReasoningTokens += Integer(reader, 5);
            target.CacheWriteTokens += cacheWrite;
            target.CacheReadTokens += cacheRead;
            target.MessageCount += 1;
        }
        return aggregate.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Provider)
            .ThenBy(item => item.Model)
            .ToList();
    }

    private static long Integer(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    /// <summary>
    /// 费率在聚合之后统一套用，费率更新无需重新读库。
    /// 网关 provider 用 CPA 配置费率（中转站真实扣费口径）；
    /// 其余 provider（订阅制）按 models.dev 牌价折算等值成本。
    /// </summary>
    private static void ApplyCosts(IReadOnlyList<UsageObservation> observations, CostTable costs)
    {
        foreach (var observation in observations)
        {
            ProxyModelCost? rate = null;
            if (costs.GatewayProviderIds.Contains(observation.Provider))
            {
                costs.GatewayRates.TryGetValue(observation.Model, out rate);
            }
            rate ??= costs.Catalog is { } catalog
                && catalog.TryGetValue(observation.Model, out var metadata)
                && !metadata.Cost.IsEmpty
                    ? metadata.Cost
                    : null;
            if (rate is null) continue;
            observation.CostUsd = ((rate.Input ?? 0m) * observation.InputTokens
                + (rate.Output ?? 0m) * observation.OutputTokens
                + (rate.CacheRead ?? 0m) * observation.CacheReadTokens
                + (rate.CacheWrite ?? 0m) * observation.CacheWriteTokens)
                / 1_000_000m;
        }
    }

    private async Task<CostTable> ReadCostTableAsync(CancellationToken cancellationToken)
    {
        ProxyConfigSnapshot snapshot;
        try
        {
            snapshot = await _proxyStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException or YamlException)
        {
            // CLIProxyAPI 未安装或配置暂不可读：网关费率留空，订阅用量仍可按牌价折算
            return new CostTable([], [], await ReadCatalogAsync(cancellationToken).ConfigureAwait(false));
        }

        var rates = new Dictionary<string, ProxyModelCost>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in snapshot.Providers.SelectMany(provider => provider.Models))
        {
            if (model.Cost is null || model.Cost.IsEmpty) continue;
            rates.TryAdd(model.GetId(), model.Cost);
        }
        var gatewayRoot = snapshot.Access.GetBaseUrl().TrimEnd('/');
        var gatewayIds = await ReadGatewayProviderIdsAsync(gatewayRoot, cancellationToken).ConfigureAwait(false);
        var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return new CostTable(gatewayIds, rates, catalog);
    }

    private async Task<IReadOnlyDictionary<string, ModelMetadata>?> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _metadataProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "无法获取 models.dev 牌价，订阅 provider 用量暂不折算成本");
            return null;
        }
    }

    /// <summary>从 zcode 配置里找出 baseURL 指向本网关的 provider id（含根地址与带 /v1 的写法）。</summary>
    private async Task<HashSet<string>> ReadGatewayProviderIdsAsync(
        string gatewayRoot,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _zcodeConfigPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("provider", out var providers)
                    || providers.ValueKind != JsonValueKind.Object) continue;
                foreach (var provider in providers.EnumerateObject())
                {
                    if (!provider.Value.TryGetProperty("options", out var options)
                        || Text(options, "baseURL") is not { } baseUrl) continue;
                    if (baseUrl.TrimEnd('/').StartsWith(gatewayRoot, StringComparison.OrdinalIgnoreCase)) ids.Add(provider.Name);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "无法读取 zcode 配置 {Path}，跳过网关计费识别", path);
            }
        }
        return ids;
    }

    private DatabaseStamp? GetDatabaseStamp()
    {
        var db = GetFileStamp(_dbPath);
        if (db is null) return null;
        return new DatabaseStamp(db.Value, GetFileStamp(_dbPath + "-wal"));
    }

    private static FileStamp? GetFileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks)
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static string? Text(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return null;
        return item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString() : null;
    }

    private sealed record CostTable(
        HashSet<string> GatewayProviderIds,
        Dictionary<string, ProxyModelCost> GatewayRates,
        IReadOnlyDictionary<string, ModelMetadata>? Catalog);

    private readonly record struct DatabaseStamp(FileStamp Database, FileStamp? Wal);
    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);
}
