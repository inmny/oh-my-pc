using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using ZstdSharp;

namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class DshUsageCollector : ILocalUsageCollector
{
    private const uint ZstdMagic = 0xFD2FB528;
    private const int StableReadAttempts = 3;
    private readonly string _sessionsRoot;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<DshUsageCollector> _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, CachedSession> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ModelMetadata>? _cachedCatalog;

    public DshUsageCollector(
        LocalToolDetector detector,
        IModelMetadataProvider metadataProvider,
        ILogger<DshUsageCollector> logger)
        : this(detector.DshSessionsRoot, metadataProvider, logger)
    {
    }

    internal DshUsageCollector(
        string sessionsRoot,
        IModelMetadataProvider metadataProvider,
        ILogger<DshUsageCollector> logger)
    {
        _sessionsRoot = sessionsRoot;
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
        if (!Directory.Exists(_sessionsRoot)) return [];

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
                _logger.LogWarning(exception, "无法获取 models.dev 牌价，DSH 用量暂不折算成本");
            }
        }

        // DSH 升级后会话文件名为 session.v3.jsonl.zstd（旧版为 session.jsonl.zstd），通配同时覆盖两种
        // 扫描范围含归档/备份目录（~/.dsh/sessions-*）：归档里有更早的历史会话。
        // 同一会话（目录名 session-<uuid> 相同）可能同时存在于主目录与归档——按 uuid 去重，取修改时间最新的一份。
        var dshHome = Path.GetDirectoryName(_sessionsRoot);
        var roots = new List<string> { _sessionsRoot };
        if (dshHome is not null && Directory.Exists(dshHome))
        {
            roots.AddRange(Directory.GetDirectories(dshHome, "sessions-*"));
        }

        var paths = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "session*.jsonl.zstd", SearchOption.AllDirectories))
            .GroupBy(path => Path.GetFileName(Path.GetDirectoryName(path)!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(File.GetLastWriteTimeUtc).First())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentPaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggregate = new Dictionary<(DateOnly Date, string Provider, string Model), UsageObservation>();
        var cacheHits = 0;
        var parsedFiles = 0;
        long bytesRead = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = GetFileStamp(path);
            if (stamp is null) continue;

            _sessionCache.TryGetValue(path, out var previous);
            if (previous is not null && previous.Stamp == stamp.Value)
            {
                cacheHits++;
                MergeSession(aggregate, previous.Observations, fullHistory, today, observedAt);
                continue;
            }

            try
            {
                var (compressed, stableStamp) = await ReadStableSessionAsync(path, cancellationToken).ConfigureAwait(false);
                bytesRead += compressed.LongLength;
                var observations = ParseSession(compressed, fullHistory: true, today, deviceId, observedAt);
                if (_cachedCatalog is { Count: > 0 })
                {
                    // 目录未就绪（缺失或为空）时费用按 0 记且不入缓存：目录就绪后的下一轮会重新解析计价
                    ApplyCatalogCosts(observations, _cachedCatalog);
                    _sessionCache[path] = new CachedSession(stableStamp, observations);
                }
                parsedFiles++;
                MergeSession(aggregate, observations, fullHistory, today, observedAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (previous?.Observations is not null)
                {
                    _logger.LogWarning(exception, "无法读取 DSH 会话 {Path}，继续使用上次有效快照", path);
                    MergeSession(aggregate, previous.Observations, fullHistory, today, observedAt);
                }
                else if (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"DSH 会话暂时不可读：{path}", exception);
                }
                else
                {
                    _logger.LogWarning(exception, "DSH 会话格式无效，已跳过 {Path}", path);
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
            "DSH 用量采集完成：{FileCount} 个会话，缓存命中 {CacheHits}，重新解析 {ParsedFiles}，读取 {BytesRead} 字节，耗时 {ElapsedMs} ms",
            paths.Length,
            cacheHits,
            parsedFiles,
            bytesRead,
            stopwatch.ElapsedMilliseconds);
        return aggregate.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Provider)
            .ThenBy(item => item.Model)
            .ToList();
    }

    internal static IReadOnlyList<UsageObservation> ParseSession(
        ReadOnlySpan<byte> compressed,
        bool fullHistory,
        DateOnly today,
        string deviceId,
        DateTimeOffset observedAt)
    {
        var frames = ScanFrames(compressed);
        if (frames.Count == 0) throw new InvalidDataException("DSH 会话不包含完整的 Zstandard 帧");

        var aggregate = new Dictionary<(DateOnly Date, string Provider, string Model), UsageObservation>();
        var headerRead = false;
        long seedLength = 0;
        string? requestProvider = null;
        string? requestModel = null;

        using var decompressor = new Decompressor();
        foreach (var frame in frames)
        {
            var plaintext = decompressor.Unwrap(compressed.Slice(frame.Offset, frame.Length));
            if (plaintext.Length == 0 || plaintext[^1] != (byte)'\n')
            {
                throw new InvalidDataException("DSH 会话帧包含不完整的 JSONL 记录");
            }

            using var reader = new StringReader(Encoding.UTF8.GetString(plaintext));
            while (reader.ReadLine() is { Length: > 0 } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (!headerRead)
                {
                    if (type != "session") throw new InvalidDataException("DSH 会话缺少头记录");
                    if (root.TryGetProperty("seedLength", out var seed)) seedLength = seed.GetInt64();
                    headerRead = true;
                    continue;
                }

                if (type == "request/context")
                {
                    var context = root.GetProperty("data");
                    requestProvider = Text(context, "provider") ?? requestProvider;
                    requestModel = Text(context, "model") ?? requestModel;
                    continue;
                }

                if (type == "request/header")
                {
                    var config = root.GetProperty("data").GetProperty("header").GetProperty("config");
                    requestProvider = Text(config, "provider") ?? requestProvider;
                    requestModel = Text(config, "model") ?? requestModel;
                    continue;
                }

                if (type != "assistant/message" || root.GetProperty("seq").GetInt64() < seedLength) continue;

                var data = root.GetProperty("data");
                if (!data.TryGetProperty("usage", out var usage)) continue;

                var source = data.GetProperty("message").TryGetProperty("source", out var messageSource)
                    ? messageSource
                    : default;
                var provider = Text(source, "provider") ?? requestProvider ?? "unknown";
                var model = Text(source, "model") ?? requestModel ?? "unknown";
                var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("time").GetInt64());
                var date = DateOnly.FromDateTime(eventTime.LocalDateTime);
                if (!fullHistory && date != today) continue;

                var key = (date, provider, model);
                if (!aggregate.TryGetValue(key, out var target))
                {
                    target = new UsageObservation
                    {
                        Date = date,
                        DeviceId = deviceId,
                        Client = "dsh",
                        Provider = provider,
                        Model = model,
                        ObservedAt = observedAt
                    };
                    aggregate[key] = target;
                }

                var inputTokens = Integer(usage, "inputTokens");
                var outputTokens = Integer(usage, "outputTokens");
                var cacheReadTokens = Integer(usage, "cacheReadTokens");
                var cacheWriteTokens = Integer(usage, "cacheWriteTokens");
                target.InputTokens += inputTokens;
                target.OutputTokens += outputTokens;
                target.CacheReadTokens += cacheReadTokens;
                target.CacheWriteTokens += cacheWriteTokens;
                target.ReasoningTokens += Integer(usage, "reasoningTokens");
                target.MessageCount += 1;
            }
        }

        if (!headerRead) throw new InvalidDataException("DSH 会话缺少头记录");
        return aggregate.Values.ToList();
    }

    private static IReadOnlyList<(int Offset, int Length)> ScanFrames(ReadOnlySpan<byte> source)
    {
        var frames = new List<(int Offset, int Length)>();
        var offset = 0;
        while (offset < source.Length)
        {
            var start = offset;
            if (source.Length - offset < 4) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]) != ZstdMagic)
            {
                throw new InvalidDataException($"DSH 会话在字节 {offset} 处包含无效的 Zstandard 帧");
            }

            offset += 4;
            if (offset == source.Length) break;
            var descriptor = source[offset++];
            if ((descriptor & 0x18) != 0) throw new InvalidDataException("DSH 会话包含无效的 Zstandard 帧头");

            var contentSizeFlag = descriptor >> 6;
            var singleSegment = (descriptor & 0x20) != 0;
            var checksum = (descriptor & 0x04) != 0;
            var dictionaryFlag = descriptor & 0x03;
            var dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
            var contentSizeBytes = contentSizeFlag == 0 ? singleSegment ? 1 : 0 : 1 << contentSizeFlag;
            var remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
            if (source.Length - offset < remainingHeaderBytes) break;
            offset += remainingHeaderBytes;

            var complete = false;
            while (!complete)
            {
                if (source.Length - offset < 3) return frames;
                var blockHeader = source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16;
                offset += 3;
                var blockType = blockHeader >> 1 & 0x03;
                if (blockType == 3) throw new InvalidDataException("DSH 会话包含无效的 Zstandard 数据块");
                var blockSize = blockHeader >> 3;
                var payloadBytes = blockType == 1 ? 1 : blockSize;
                if (source.Length - offset < payloadBytes) return frames;
                offset += payloadBytes;
                complete = (blockHeader & 1) != 0;
            }

            if (checksum)
            {
                if (source.Length - offset < 4) break;
                offset += 4;
            }

            frames.Add((start, offset - start));
        }

        return frames;
    }

    private static void MergeSession(
        IDictionary<(DateOnly Date, string Provider, string Model), UsageObservation> aggregate,
        IReadOnlyList<UsageObservation>? observations,
        bool fullHistory,
        DateOnly today,
        DateTimeOffset observedAt)
    {
        if (observations is null) return;
        foreach (var observation in observations)
        {
            if (!fullHistory && observation.Date != today) continue;
            Merge(aggregate, observation, observedAt);
        }
    }

    private static void Merge(
        IDictionary<(DateOnly Date, string Provider, string Model), UsageObservation> aggregate,
        UsageObservation observation,
        DateTimeOffset observedAt)
    {
        var key = (observation.Date, observation.Provider, observation.Model);
        if (!aggregate.TryGetValue(key, out var target))
        {
            target = new UsageObservation
            {
                Date = observation.Date,
                DeviceId = observation.DeviceId,
                Client = observation.Client,
                Provider = observation.Provider,
                Model = observation.Model,
                ObservedAt = observedAt
            };
            aggregate[key] = target;
        }

        target.InputTokens += observation.InputTokens;
        target.OutputTokens += observation.OutputTokens;
        target.CacheReadTokens += observation.CacheReadTokens;
        target.CacheWriteTokens += observation.CacheWriteTokens;
        target.ReasoningTokens += observation.ReasoningTokens;
        target.MessageCount += observation.MessageCount;
        target.ActiveTimeMs += observation.ActiveTimeMs;
        target.CostUsd += observation.CostUsd;
    }

    internal static FileStream OpenSessionReadStream(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<(byte[] Bytes, FileStamp Stamp)> ReadStableSessionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = GetFileStamp(path) ?? throw new FileNotFoundException("DSH 会话文件不存在。", path);
            var snapshot = await ReadSessionBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var after = GetFileStamp(path);
            if (after == before && snapshot.LongLength == before.Length) return (snapshot, before);
        }

        throw new IOException($"DSH 会话在读取期间持续变化：{path}");
    }

    private static async Task<byte[]> ReadSessionBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenSessionReadStream(path);
        if (stream.Length > Array.MaxLength)
        {
            throw new IOException($"DSH 会话文件过大：{path}");
        }

        var snapshot = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        await stream.ReadExactlyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static FileStamp? GetFileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks)
            : null;
    }

    /// <summary>费用统一按 models.dev 牌价折算（每百万 token 美元）；模型名先归一到目录标准 id，未命中不计费。</summary>
    private static void ApplyCatalogCosts(
        IReadOnlyList<UsageObservation> observations,
        IReadOnlyDictionary<string, ModelMetadata>? catalog)
    {
        if (catalog is null) return;
        foreach (var observation in observations)
        {
            if (ModelMetadataParser.Find(catalog, observation.Model) is not { Cost.IsEmpty: false } metadata) continue;
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

    private static long Integer(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return 0;
        return item.GetInt64();
    }

    private sealed record CachedSession(FileStamp Stamp, IReadOnlyList<UsageObservation>? Observations);
    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);
}
