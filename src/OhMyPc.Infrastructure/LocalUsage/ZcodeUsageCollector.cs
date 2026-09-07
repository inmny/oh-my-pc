using System.Diagnostics;
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
/// 计费统一按 models.dev 牌价折算等值成本；别名（如 GPT-5.6-Sol）经 CPA 配置的别名表归一到真实名再查目录。
/// </summary>
public sealed class ZcodeUsageCollector : ILocalUsageCollector
{
    private readonly string _dbPath;
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
        : this(detector.ZcodeDatabasePath, proxyStore, metadataProvider, logger)
    {
    }

    internal ZcodeUsageCollector(
        string dbPath,
        IProxyConfigStore proxyStore,
        IModelMetadataProvider metadataProvider,
        ILogger<ZcodeUsageCollector> logger)
    {
        _dbPath = dbPath;
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

        var costs = await ReadCatalogTableAsync(cancellationToken).ConfigureAwait(false);
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
    /// 费率统一按 models.dev 牌价折算（每百万 token 美元）：别名/变体名先归一到目录标准 id 再查价，
    /// 未命中目录的模型不计费。
    /// </summary>
    private static void ApplyCosts(IReadOnlyList<UsageObservation> observations, CatalogTable costs)
    {
        if (costs.Catalog is null) return;
        foreach (var observation in observations)
        {
            if (ModelMetadataParser.Find(costs.Catalog, ModelMetadataParser.Canonicalize(costs.Catalog, observation.Model, costs.AliasToName))
                is not { Cost.IsEmpty: false } metadata) continue;
            var rate = metadata.Cost;
            observation.CostUsd = ((rate.Input ?? 0m) * observation.InputTokens
                + (rate.Output ?? 0m) * observation.OutputTokens
                + (rate.CacheRead ?? 0m) * observation.CacheReadTokens
                + (rate.CacheWrite ?? 0m) * observation.CacheWriteTokens)
                / 1_000_000m;
        }
    }

    /// <summary>费率与名称归一唯一来源是 models.dev 目录：别名表（CPA 配置）把别名折算成真实名后查目录。</summary>
    private async Task<CatalogTable> ReadCatalogTableAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> aliasToName = new Dictionary<string, string>();
        try
        {
            aliasToName = (await _proxyStore.LoadAsync(cancellationToken).ConfigureAwait(false)).AliasToName;
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException or YamlException)
        {
            // CLIProxyAPI 未安装或配置暂不可读：无别名可归一，直接按上报名查目录
        }
        var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogTable(aliasToName, catalog);
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
            _logger.LogWarning(exception, "无法获取 models.dev 牌价，zcode 用量暂不折算成本");
            return null;
        }
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

    private sealed record CatalogTable(
        IReadOnlyDictionary<string, string> AliasToName,
        IReadOnlyDictionary<string, ModelMetadata>? Catalog);

    private readonly record struct DatabaseStamp(FileStamp Database, FileStamp? Wal);
    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);
}
