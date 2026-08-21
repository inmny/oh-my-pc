using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.InputStatus;

public sealed class InputStatusRefreshService(
    IInputStatusClient client,
    IAppStore store,
    IAutomationEventPublisher eventPublisher,
    ITextLocalizer text,
    ILogger<InputStatusRefreshService> logger)
{
    private const int MaximumSamples = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly Dictionary<string, InputSourceStatusSnapshot> _snapshots = [];
    private readonly Dictionary<string, long> _generations = [];

    public event EventHandler? Refreshed;

    public InputSourceStatusSnapshot? GetSnapshot(string sourceId)
    {
        lock (_snapshotLock) return _snapshots.GetValueOrDefault(sourceId);
    }

    public IReadOnlyList<InputSourceStatusSnapshot> ListSnapshots()
    {
        lock (_snapshotLock) return _snapshots.Values.ToArray();
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sources = (await store.ListDataSourcesAsync(cancellationToken))
                .Where(IsMonitored)
                .ToArray();
            var sourceIds = sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
            lock (_snapshotLock)
            {
                foreach (var staleId in _snapshots.Keys.Where(id => !sourceIds.Contains(id)).ToArray())
                {
                    InvalidateSourceCore(staleId);
                }
            }

            foreach (var source in sources)
            {
                await RefreshCoreAsync(source, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = await store.GetDataSourceAsync(sourceId, cancellationToken);
            if (source is null || !IsMonitored(source))
            {
                RemoveSnapshot(sourceId);
            }
            else
            {
                await RefreshCoreAsync(source, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public void InvalidateSource(string sourceId) => RemoveSnapshot(sourceId);

    private async Task RefreshCoreAsync(DataSourceDefinition source, CancellationToken cancellationToken)
    {
        var generation = GetGeneration(source.Id);
        var attemptedAt = DateTimeOffset.UtcNow;
        if (!Uri.TryCreate(source.ModelStatusUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            if (IsGenerationCurrent(source.Id, generation))
            {
                SetFailure(source, generation, attemptedAt, "模型状态地址不是有效的 HTTP(S) URL。");
            }
            return;
        }

        var statusUrl = endpoint.AbsoluteUri;
        if (!PrepareEndpoint(source.Id, statusUrl, generation)) return;

        IReadOnlyList<InputModelStatus> models;
        try
        {
            models = await client.GetModelsAsync(endpoint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法刷新模型状态来源 {Source}", source.Name);
            if (IsGenerationCurrent(source.Id, generation))
            {
                SetFailure(source, generation, attemptedAt, exception.Message);
            }
            return;
        }

        if (!IsGenerationCurrent(source.Id, generation)) return;
        var previous = GetSnapshot(source.Id);
        var previousModels = previous is not null
            && string.Equals(previous.StatusUrl, statusUrl, StringComparison.Ordinal)
                ? previous.Models.ToDictionary(model => model.Model, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, InputModelStatusSnapshot>(StringComparer.OrdinalIgnoreCase);
        var currentModels = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Model))
            .GroupBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select(model => ToSnapshot(model, previousModels.GetValueOrDefault(model.Model)))
            .ToArray();

        if (!TrySetSnapshot(new InputSourceStatusSnapshot
        {
            SourceId = source.Id,
            StatusUrl = statusUrl,
            Models = currentModels,
            LastAttemptAt = attemptedAt,
            LastSuccessAt = attemptedAt
        }, generation)) return;

        foreach (var model in models)
        {
            if (!IsGenerationCurrent(source.Id, generation)) return;
            try
            {
                await PublishChangeAsync(source, endpoint, model, generation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "无法发布模型状态事件 {Source} {Model}", source.Name, model.Model);
            }
        }
    }

    private async Task PublishChangeAsync(
        DataSourceDefinition source,
        Uri endpoint,
        InputModelStatus model,
        long generation,
        CancellationToken cancellationToken)
    {
        var endpointKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
        var stateKey = $"input-status:source:{source.Id}:endpoint:{endpointKey}:model:{model.Model}";
        var previous = await store.GetSourceStateAsync(stateKey, cancellationToken);
        if (!IsGenerationCurrent(source.Id, generation)) return;
        if (previous is null)
        {
            await SaveStateAsync(stateKey, model.Available, cancellationToken);
            return;
        }

        var previousAvailable = JsonSerializer.Deserialize<bool>(previous.ValueJson);
        if (previousAvailable == model.Available || !IsGenerationCurrent(source.Id, generation)) return;

        var fields = new JsonObject
        {
            ["sourceId"] = source.Id,
            ["model"] = model.Model,
            ["available"] = model.Available,
            ["previousAvailable"] = previousAvailable
        };
        if (model.LatencyMilliseconds is not null) fields["latencyMs"] = (double)model.LatencyMilliseconds.Value;
        if (model.Error is not null) fields["error"] = model.Error;

        await eventPublisher.PublishAsync(new AutomationEvent
        {
            Type = AutomationEventTypes.InputModelAvailabilityChanged,
            SourceId = source.Id,
            SubjectKey = stateKey,
            Title = source.Name,
            Body = model.Available
                ? text.Format("Notification_InputModelAvailable", model.Model, model.LatencyMilliseconds ?? 0)
                : text.Format("Notification_InputModelUnavailable", model.Model, model.Error ?? text["Notification_NoErrorDetail"]),
            Fields = fields
        }, cancellationToken);

        if (IsGenerationCurrent(source.Id, generation))
        {
            await SaveStateAsync(stateKey, model.Available, cancellationToken);
        }
    }

    private void SetFailure(DataSourceDefinition source, long generation, DateTimeOffset attemptedAt, string error)
    {
        var hasValidEndpoint = Uri.TryCreate(source.ModelStatusUrl, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https";
        var statusUrl = hasValidEndpoint ? endpoint!.AbsoluteUri : source.ModelStatusUrl.Trim();
        var previous = GetSnapshot(source.Id);
        var sameEndpoint = previous is not null
            && string.Equals(previous.StatusUrl, statusUrl, StringComparison.Ordinal);
        TrySetSnapshot(new InputSourceStatusSnapshot
        {
            SourceId = source.Id,
            StatusUrl = statusUrl,
            Models = sameEndpoint ? previous!.Models : [],
            LastAttemptAt = attemptedAt,
            LastSuccessAt = sameEndpoint ? previous!.LastSuccessAt : null,
            Error = error
        }, generation);
    }

    private static InputModelStatusSnapshot ToSnapshot(
        InputModelStatus model,
        InputModelStatusSnapshot? previous)
    {
        var samples = (previous?.Samples ?? [])
            .Append(model.Available)
            .TakeLast(MaximumSamples)
            .ToArray();
        return new InputModelStatusSnapshot
        {
            Model = model.Model,
            Available = model.Available,
            LatencyMilliseconds = model.LatencyMilliseconds,
            Error = model.Error,
            Samples = samples
        };
    }

    private static bool IsMonitored(DataSourceDefinition source) =>
        source.Enabled && !string.IsNullOrWhiteSpace(source.ModelStatusUrl);

    private bool PrepareEndpoint(string sourceId, string statusUrl, long generation)
    {
        lock (_snapshotLock)
        {
            if (_generations.GetValueOrDefault(sourceId) != generation) return false;
            if (_snapshots.TryGetValue(sourceId, out var previous)
                && !string.Equals(previous.StatusUrl, statusUrl, StringComparison.Ordinal))
            {
                _snapshots.Remove(sourceId);
            }
            return true;
        }
    }

    private bool TrySetSnapshot(InputSourceStatusSnapshot snapshot, long generation)
    {
        lock (_snapshotLock)
        {
            if (_generations.GetValueOrDefault(snapshot.SourceId) != generation) return false;
            _snapshots[snapshot.SourceId] = snapshot;
            return true;
        }
    }

    private long GetGeneration(string sourceId)
    {
        lock (_snapshotLock) return _generations.GetValueOrDefault(sourceId);
    }

    private bool IsGenerationCurrent(string sourceId, long generation)
    {
        lock (_snapshotLock) return _generations.GetValueOrDefault(sourceId) == generation;
    }

    private void RemoveSnapshot(string sourceId)
    {
        lock (_snapshotLock) InvalidateSourceCore(sourceId);
    }

    private void InvalidateSourceCore(string sourceId)
    {
        _snapshots.Remove(sourceId);
        _generations[sourceId] = _generations.GetValueOrDefault(sourceId) + 1;
    }

    private Task SaveStateAsync(string key, bool available, CancellationToken cancellationToken) =>
        store.SaveSourceStateAsync(new AutomationSourceState
        {
            Key = key,
            ValueJson = JsonSerializer.Serialize(available),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
}
