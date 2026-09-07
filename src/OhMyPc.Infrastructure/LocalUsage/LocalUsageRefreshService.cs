using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class LocalUsageRefreshedEventArgs(bool fullHistory) : EventArgs
{
    public bool FullHistory { get; } = fullHistory;
}

public sealed class LocalUsageRefreshService(
    ILocalUsageCollector collector,
    IAppStore store,
    IProxyConfigStore proxyStore,
    IModelMetadataProvider metadataProvider,
    IAutomationEventPublisher eventPublisher,
    ITextLocalizer text,
    ILogger<LocalUsageRefreshService> logger)
{
    private const string DefaultProviderId = "unknown";
    private const string DefaultModelId = "unknown";
    private const string ActivityClient = "_activity";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _aliasToName;
    private IReadOnlyDictionary<string, ModelMetadata>? _catalog;
    private IReadOnlyList<UsageSnapshot>? _lastFullSnapshot;
    private IReadOnlyList<UsageSnapshot>? _lastTodaySnapshot;
    private DateOnly? _lastFullSnapshotDate;
    private DateOnly? _lastTodaySnapshotDate;
    public event EventHandler<LocalUsageRefreshedEventArgs>? Refreshed;

    public async Task RefreshAsync(bool fullHistory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var collectedObservations = await Task.Run(
                () => collector.CollectAsync(fullHistory, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var collectionElapsedMs = stopwatch.ElapsedMilliseconds;
            if (_catalog is null || _aliasToName is null) await LoadNamingAsync(cancellationToken).ConfigureAwait(false);
            var todayDate = DateOnly.FromDateTime(DateTime.Now);
            // 模型名在入库前统一到目录标准 id：别名与变体名共用同一行，费率与分组按同一口径
            var observations = CanonicalizeModels(fullHistory
                ? collectedObservations
                : PreserveFullHistoryActivity(collectedObservations, todayDate));
            var snapshot = CreateSnapshot(observations);
            var previousSnapshot = fullHistory ? _lastFullSnapshot : _lastTodaySnapshot;
            var previousDate = fullHistory ? _lastFullSnapshotDate : _lastTodaySnapshotDate;
            if (previousDate == todayDate && previousSnapshot is not null && previousSnapshot.SequenceEqual(snapshot))
            {
                await PublishTodayUsageAsync(cancellationToken).ConfigureAwait(false);
                logger.LogDebug(
                    "本地用量未变化，跳过持久化和界面更新（{Mode}，采集耗时 {ElapsedMs} ms）",
                    fullHistory ? "history" : "today",
                    collectionElapsedMs);
                return;
            }

            var scopes = snapshot
                .Concat(previousSnapshot ?? [])
                .Select(item => new UsageObservationScope(item.Date, item.DeviceId))
                .Distinct()
                .ToArray();
            await store.ReplaceUsageAsync(observations, scopes, cancellationToken).ConfigureAwait(false);
            var persistenceElapsedMs = stopwatch.ElapsedMilliseconds - collectionElapsedMs;
            await PublishTodayUsageAsync(cancellationToken).ConfigureAwait(false);

            if (fullHistory)
            {
                _lastFullSnapshot = snapshot;
                _lastFullSnapshotDate = todayDate;
            }
            _lastTodaySnapshot = CreateSnapshot(observations.Where(item => item.Date == todayDate));
            _lastTodaySnapshotDate = todayDate;

            stopwatch.Stop();
            logger.LogInformation(
                "本地用量已刷新：{Count} 条观测（{Mode}），采集 {CollectionMs} ms，持久化 {PersistenceMs} ms，总计 {TotalMs} ms",
                observations.Count,
                fullHistory ? "history" : "today",
                collectionElapsedMs,
                persistenceElapsedMs,
                stopwatch.ElapsedMilliseconds);
            Refreshed?.Invoke(this, new LocalUsageRefreshedEventArgs(fullHistory));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadNamingAsync(CancellationToken cancellationToken)
    {
        try
        {
            _aliasToName = (await proxyStore.LoadAsync(cancellationToken).ConfigureAwait(false)).AliasToName;
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            _aliasToName = new Dictionary<string, string>();
        }
        try
        {
            _catalog = await metadataProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "无法获取 models.dev 目录，模型名暂按原样记录");
            _catalog = new Dictionary<string, ModelMetadata>();
        }
    }

    /// <summary>把观测行的模型名归一到目录标准 id（别名/变体合并到同一行）；活动行与目录未命中者保持原样。</summary>
    private IReadOnlyList<UsageObservation> CanonicalizeModels(IReadOnlyList<UsageObservation> observations)
    {
        var result = new List<UsageObservation>(observations.Count);
        var merged = new Dictionary<(DateOnly, string, string, string, string), UsageObservation>();
        foreach (var item in observations)
        {
            var model = item.Client == ActivityClient || item.Model is DefaultModelId or ""
                ? item.Model
                : ModelMetadataParser.Canonicalize(_catalog!, item.Model, _aliasToName);
            if (merged.TryGetValue((item.Date, item.DeviceId, item.Client, item.Provider, model), out var target))
            {
                target.InputTokens += item.InputTokens;
                target.OutputTokens += item.OutputTokens;
                target.CacheReadTokens += item.CacheReadTokens;
                target.CacheWriteTokens += item.CacheWriteTokens;
                target.ReasoningTokens += item.ReasoningTokens;
                target.MessageCount += item.MessageCount;
                target.ActiveTimeMs += item.ActiveTimeMs;
                target.CostUsd += item.CostUsd;
                continue;
            }
            var canonical = new UsageObservation
            {
                Date = item.Date,
                DeviceId = item.DeviceId,
                Client = item.Client,
                Provider = item.Provider,
                Model = model,
                InputTokens = item.InputTokens,
                OutputTokens = item.OutputTokens,
                CacheReadTokens = item.CacheReadTokens,
                CacheWriteTokens = item.CacheWriteTokens,
                ReasoningTokens = item.ReasoningTokens,
                MessageCount = item.MessageCount,
                ActiveTimeMs = item.ActiveTimeMs,
                CostUsd = item.CostUsd,
                ObservedAt = item.ObservedAt
            };
            merged[(canonical.Date, canonical.DeviceId, canonical.Client, canonical.Provider, model)] = canonical;
            result.Add(canonical);
        }
        return result;
    }

    private IReadOnlyList<UsageObservation> PreserveFullHistoryActivity(
        IReadOnlyList<UsageObservation> observations,
        DateOnly today)
    {
        var activityRows = _lastFullSnapshot?
            .Where(item => item.Date == today && item.Client == "_activity")
            .ToArray();
        if (activityRows is not { Length: > 0 }) return observations;

        var result = observations.ToList();
        var keys = result
            .Select(item => (item.Date, item.DeviceId, item.Client, item.Provider, item.Model))
            .ToHashSet();
        var observedAt = DateTimeOffset.UtcNow;
        foreach (var item in activityRows)
        {
            if (!keys.Add((item.Date, item.DeviceId, item.Client, item.Provider, item.Model))) continue;
            result.Add(new UsageObservation
            {
                Date = item.Date,
                DeviceId = item.DeviceId,
                Client = item.Client,
                Provider = item.Provider,
                Model = item.Model,
                InputTokens = item.InputTokens,
                OutputTokens = item.OutputTokens,
                CacheReadTokens = item.CacheReadTokens,
                CacheWriteTokens = item.CacheWriteTokens,
                ReasoningTokens = item.ReasoningTokens,
                MessageCount = item.MessageCount,
                ActiveTimeMs = item.ActiveTimeMs,
                CostUsd = item.CostUsd,
                ObservedAt = observedAt
            });
        }
        return result;
    }

    private async Task PublishTodayUsageAsync(CancellationToken cancellationToken)
    {
        var today = await store.GetTodayUsageAsync(cancellationToken).ConfigureAwait(false);
        await eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.DailyUsageUpdated,
            SourceId = "local-usage",
            SubjectKey = "local:daily-tokens",
            Title = text["Notification_LocalUsageTitle"],
            Body = text.Format("Notification_DailyTokens", today.TotalTokens),
            Fields = new JsonObject
            {
                ["totalTokens"] = (double)today.TotalTokens,
                ["date"] = today.Date.ToString("yyyy-MM-dd")
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static UsageSnapshot[] CreateSnapshot(IEnumerable<UsageObservation> observations) => observations
        .Select(item => new UsageSnapshot(
            item.Date,
            item.DeviceId,
            item.Client,
            item.Provider,
            item.Model,
            item.InputTokens,
            item.OutputTokens,
            item.CacheReadTokens,
            item.CacheWriteTokens,
            item.ReasoningTokens,
            item.MessageCount,
            item.ActiveTimeMs,
            item.CostUsd))
        .OrderBy(item => item.Date)
        .ThenBy(item => item.DeviceId, StringComparer.Ordinal)
        .ThenBy(item => item.Client, StringComparer.Ordinal)
        .ThenBy(item => item.Provider, StringComparer.Ordinal)
        .ThenBy(item => item.Model, StringComparer.Ordinal)
        .ToArray();

    private readonly record struct UsageSnapshot(
        DateOnly Date,
        string DeviceId,
        string Client,
        string Provider,
        string Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ReasoningTokens,
        long MessageCount,
        long ActiveTimeMs,
        decimal CostUsd);
}
