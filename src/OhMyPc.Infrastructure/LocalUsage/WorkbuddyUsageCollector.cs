using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using YamlDotNet.Core;

namespace OhMyPc.Infrastructure.LocalUsage;

/// <summary>
/// 读取 workbuddy 的会话转录（~/.workbuddy/projects/&lt;工程&gt;/*.jsonl）统计用量。
/// 每行 assistant 侧记录（type 为 message / function_call）带 message.usage：
/// input_tokens 为 OpenAI 口径（含缓存命中，实证 prompt_cache_hit + miss == input），
/// 缓存写入只出现在 providerData.rawUsage 的 provider 专有字段里。
/// 计费统一按 models.dev 牌价折算；别名（经 CPA 同步的网关模型 id）先归一到真实名再查目录。
/// </summary>
public sealed class WorkbuddyUsageCollector : ILocalUsageCollector
{
    private const int StableReadAttempts = 3;
    private readonly string _projectsRoot;
    private readonly IProxyConfigStore _proxyStore;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<WorkbuddyUsageCollector> _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, CachedSession> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ModelMetadata>? _cachedCatalog;

    public WorkbuddyUsageCollector(
        LocalToolDetector detector,
        IProxyConfigStore proxyStore,
        IModelMetadataProvider metadataProvider,
        ILogger<WorkbuddyUsageCollector> logger)
        : this(detector.WorkbuddyProjectsRoot, proxyStore, metadataProvider, logger)
    {
    }

    internal WorkbuddyUsageCollector(
        string projectsRoot,
        IProxyConfigStore proxyStore,
        IModelMetadataProvider metadataProvider,
        ILogger<WorkbuddyUsageCollector> logger)
    {
        _projectsRoot = projectsRoot;
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
        if (!Directory.Exists(_projectsRoot)) return [];

        var stopwatch = Stopwatch.StartNew();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var observedAt = DateTimeOffset.UtcNow;
        var deviceId = LocalUsageDevice.Id();
        if (_cachedCatalog is null || _cachedCatalog.Count == 0)
        {
            try
            {
                _cachedCatalog = await _metadataProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(exception, "无法获取 models.dev 牌价，workbuddy 用量暂不折算成本");
            }
        }

        var paths = Directory
            .EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentPaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggregate = new Dictionary<(DateOnly Date, string Provider, string Model), UsageObservation>();
        var cacheHits = 0;
        var parsedFiles = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = GetFileStamp(path);
            if (stamp is null) continue;

            _sessionCache.TryGetValue(path, out var previous);
            if (previous is not null && previous.Stamp == stamp.Value)
            {
                cacheHits++;
                MergeSession(aggregate, previous.Observations, fullHistory, today);
                continue;
            }

            try
            {
                var observations = await ReadStableSessionAsync(path, fullHistory: true, today, deviceId, cancellationToken).ConfigureAwait(false);
                if (_cachedCatalog is { Count: > 0 })
                {
                    // 目录未就绪（缺失或为空）时费用按 0 记且不入缓存：目录就绪后的下一轮会重新解析计价
                    await ApplyCatalogCosts(observations, cancellationToken).ConfigureAwait(false);
                    _sessionCache[path] = new CachedSession(stamp.Value, observations);
                }
                parsedFiles++;
                MergeSession(aggregate, observations, fullHistory, today);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (previous?.Observations is not null)
                {
                    _logger.LogWarning(exception, "无法读取 workbuddy 会话 {Path}，继续使用上次有效快照", path);
                    MergeSession(aggregate, previous.Observations, fullHistory, today);
                }
                else if (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"workbuddy 会话暂时不可读：{path}", exception);
                }
                else
                {
                    _logger.LogWarning(exception, "workbuddy 会话格式无效，已跳过 {Path}", path);
                    _sessionCache[path] = new CachedSession(stamp.Value, null);
                }
            }
        }

        foreach (var stalePath in _sessionCache.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
        {
            _sessionCache.Remove(stalePath);
        }

        stopwatch.Stop();
        _logger.LogDebug(
            "workbuddy 用量采集完成：{FileCount} 个会话，缓存命中 {CacheHits}，重新解析 {ParsedFiles}，耗时 {ElapsedMs} ms",
            paths.Length,
            cacheHits,
            parsedFiles,
            stopwatch.ElapsedMilliseconds);
        return aggregate.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Provider)
            .ThenBy(item => item.Model)
            .ToList();
    }

    internal static async Task<IReadOnlyList<UsageObservation>> ParseSessionAsync(
        string path,
        bool fullHistory,
        DateOnly today,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var aggregate = new Dictionary<(DateOnly Date, string Provider, string Model), UsageObservation>();
        using var stream = OpenSessionReadStream(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var usage = FindUsage(root);
                if (usage.ValueKind != JsonValueKind.Object) continue;

                var providerData = root.TryGetProperty("providerData", out var data) && data.ValueKind == JsonValueKind.Object
                    ? data
                    : default;
                var model = Text(providerData, "model")
                    ?? Text(providerData, "requestModelId")
                    ?? "unknown";
                var eventTime = root.TryGetProperty("timestamp", out var timestamp)
                    && timestamp.ValueKind == JsonValueKind.Number
                    && timestamp.TryGetInt64(out var milliseconds)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                        : DateTimeOffset.Now;
                var date = DateOnly.FromDateTime(eventTime.LocalDateTime);
                if (!fullHistory && date != today) continue;

                // input_tokens 为含缓存口径：换算为应用统一的不含缓存口径
                var inputTokens = Long(usage, "input_tokens");
                var outputTokens = Long(usage, "output_tokens");
                var cacheRead = Long(usage, "cache_read_input_tokens");
                // 缓存写入没有归一字段：anthropic 系在 rawUsage.cache_creation_input_tokens，
                // 智谱系在 rawUsage.prompt_cache_write_tokens
                var cacheWrite = NestedLong(providerData, "rawUsage", "cache_creation_input_tokens") is { } creation && creation > 0
                    ? creation
                    : NestedLong(providerData, "rawUsage", "prompt_cache_write_tokens");
                var rawUsage = providerData.ValueKind == JsonValueKind.Object
                    && providerData.TryGetProperty("rawUsage", out var raw)
                    && raw.ValueKind == JsonValueKind.Object
                        ? raw
                        : default;

                var key = (date, "unknown", model);
                if (!aggregate.TryGetValue(key, out var target))
                {
                    target = new UsageObservation
                    {
                        Date = date,
                        DeviceId = deviceId,
                        Client = "workbuddy",
                        Provider = "unknown",
                        Model = model
                    };
                    aggregate[key] = target;
                }

                target.InputTokens += Math.Max(0, inputTokens - cacheRead - cacheWrite);
                target.OutputTokens += outputTokens;
                target.ReasoningTokens += rawUsage.ValueKind == JsonValueKind.Object ? Long(rawUsage, "completion_thinking_tokens") : 0;
                target.CacheWriteTokens += cacheWrite;
                target.CacheReadTokens += cacheRead;
                target.MessageCount += 1;
            }
        }

        return aggregate.Values.ToList();
    }

    /// <summary>usage 挂在 message.message（assistant 回复）或 message（工具调用请求）上，两层都探测。</summary>
    private static JsonElement FindUsage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var wrapper))
        {
            if (wrapper.ValueKind == JsonValueKind.Object)
            {
                if (wrapper.TryGetProperty("usage", out var inner) && inner.ValueKind == JsonValueKind.Object) return inner;
                if (wrapper.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.Object
                    && nested.TryGetProperty("usage", out var nestedUsage)
                    && nestedUsage.ValueKind == JsonValueKind.Object)
                {
                    return nestedUsage;
                }
            }
        }

        return root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? usage
            : default;
    }

    private static void MergeSession(
        IDictionary<(DateOnly Date, string Provider, string Model), UsageObservation> aggregate,
        IReadOnlyList<UsageObservation>? observations,
        bool fullHistory,
        DateOnly today)
    {
        if (observations is null) return;
        foreach (var observation in observations)
        {
            if (!fullHistory && observation.Date != today) continue;
            var key = (observation.Date, observation.Provider, observation.Model);
            if (!aggregate.TryGetValue(key, out var target))
            {
                aggregate[key] = observation;
                continue;
            }

            target.InputTokens += observation.InputTokens;
            target.OutputTokens += observation.OutputTokens;
            target.CacheReadTokens += observation.CacheReadTokens;
            target.CacheWriteTokens += observation.CacheWriteTokens;
            target.ReasoningTokens += observation.ReasoningTokens;
            target.MessageCount += observation.MessageCount;
            target.CostUsd += observation.CostUsd;
        }
    }

    internal static FileStream OpenSessionReadStream(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<IReadOnlyList<UsageObservation>> ReadStableSessionAsync(
        string path, bool fullHistory, DateOnly today, string deviceId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = GetFileStamp(path) ?? throw new FileNotFoundException("workbuddy 会话文件不存在。", path);
            var observations = await ParseSessionAsync(path, fullHistory, today, deviceId, cancellationToken).ConfigureAwait(false);
            var after = GetFileStamp(path);
            if (after == before) return observations;
        }

        throw new IOException($"workbuddy 会话在读取期间持续变化：{path}");
    }

    private static FileStamp? GetFileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks)
            : null;
    }

    /// <summary>费用统一按 models.dev 牌价折算（每百万 token 美元）；别名先经 CPA 配置归一到真实名再查目录。</summary>
    private async Task ApplyCatalogCosts(
        IReadOnlyList<UsageObservation> observations,
        CancellationToken cancellationToken)
    {
        if (_cachedCatalog is null) return;
        IReadOnlyDictionary<string, string> aliasToName = new Dictionary<string, string>();
        try
        {
            aliasToName = (await _proxyStore.LoadAsync(cancellationToken).ConfigureAwait(false)).AliasToName;
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException or YamlException)
        {
            // CLIProxyAPI 未安装或配置暂不可读：无别名可归一，直接按上报名查目录
        }

        foreach (var observation in observations)
        {
            if (ModelMetadataParser.Find(_cachedCatalog, ModelMetadataParser.Canonicalize(_cachedCatalog, observation.Model, aliasToName))
                is not { Cost.IsEmpty: false } metadata) continue;
            var rate = metadata.Cost;
            observation.CostUsd = ((rate.Input ?? 0m) * observation.InputTokens
                + (rate.Output ?? 0m) * observation.OutputTokens
                + (rate.CacheRead ?? 0m) * observation.CacheReadTokens
                + (rate.CacheWrite ?? 0m) * observation.CacheWriteTokens)
                / 1_000_000m;
        }
    }

    private static string? Text(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return null;
        return item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString() : null;
    }

    private static long Long(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return 0;
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : 0;
    }

    private static long NestedLong(JsonElement value, string container, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(container, out var inner)) return 0;
        return Long(inner, property);
    }

    private sealed record CachedSession(FileStamp Stamp, IReadOnlyList<UsageObservation>? Observations);
    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);
}
