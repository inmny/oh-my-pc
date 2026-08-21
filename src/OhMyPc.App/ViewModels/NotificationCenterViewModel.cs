using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Notifications;

namespace OhMyPc.App.ViewModels;

public enum NotificationCenterPeriod
{
    All,
    Today,
    LastSevenDays
}

public sealed record NotificationSourceOption(string? Value, string DisplayName);
public sealed record NotificationSeverityOption(NotificationSeverity? Value, string DisplayName);
public sealed record NotificationPeriodOption(NotificationCenterPeriod Value, string DisplayName);

public sealed class NotificationHistoryItemViewModel(NotificationRecord record, LocalizationService text)
{
    public NotificationRecord Record { get; } = record;
    public string Id => Record.Id;
    public string Source => Record.Source;
    public string Title => Record.Title;
    public string Body => Record.Body;
    public NotificationSeverity Severity => Record.Severity;
    public string CreatedAtText => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string OriginText => text.GetEnum(Record.Origin);
    public string SourceText => $"{OriginText} · {Record.Source}";
    public string SeverityText => text.GetEnum(Record.Severity);
    public string ChannelsText => text.GetEnum(Record.Channels);
}

public sealed class NotificationCenterViewModel : ViewModelBase, IDisposable
{
    private readonly IAppStore _store;
    private readonly NotificationCenterService _notifications;
    private readonly DesktopNotificationSink _desktopNotifications;
    private readonly LocalizationService _text;
    private readonly ILogger<NotificationCenterViewModel> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, NotificationRecord> _liveRecords = [];
    private readonly HashSet<string> _hiddenIds = [];
    private CancellationTokenSource? _queryCancellation;
    private long _queryVersion;
    private bool _loaded;
    private int _retentionDays = NotificationRetentionPolicy.DefaultDays;
    private bool _isHistorySelected = true;
    private bool _isLoading;
    private string _errorText = "";
    private DateTimeOffset? _clearedThrough;
    private NotificationHistoryItemViewModel? _selectedNotification;
    private AutomationRuleDefinition? _selectedRule;
    private NotificationSourceOption? _selectedSource;
    private NotificationSeverityOption? _selectedSeverity;
    private NotificationPeriodOption? _selectedPeriod;

    public NotificationCenterViewModel(
        IAppStore store,
        NotificationCenterService notifications,
        DesktopNotificationSink desktopNotifications,
        LocalizationService text,
        ILogger<NotificationCenterViewModel> logger)
    {
        _store = store;
        _notifications = notifications;
        _desktopNotifications = desktopNotifications;
        _text = text;
        _logger = logger;
        BuildFilterOptions();
        _desktopNotifications.Published += DesktopNotifications_Published;
    }

    public ObservableCollection<NotificationHistoryItemViewModel> Notifications { get; } = [];
    public ObservableCollection<AutomationRuleDefinition> Rules { get; } = [];
    public ObservableCollection<NotificationSourceOption> SourceOptions { get; } = [];
    public ObservableCollection<NotificationSeverityOption> SeverityOptions { get; } = [];
    public ObservableCollection<NotificationPeriodOption> PeriodOptions { get; } = [];

    public bool IsHistorySelected
    {
        get => _isHistorySelected;
        set
        {
            if (!value || !Set(ref _isHistorySelected, true)) return;
            Raise(nameof(IsRulesSelected));
        }
    }

    public bool IsRulesSelected
    {
        get => !_isHistorySelected;
        set
        {
            if (!value || !Set(ref _isHistorySelected, false, nameof(IsHistorySelected))) return;
            Raise();
        }
    }

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool IsHistoryEmpty => !IsLoading && Notifications.Count == 0;
    public bool HasSelectedNotification => SelectedNotification is not null;
    public bool HasSelectedRule => SelectedRule is not null;
    public string HistoryCountText => _text.Format("NotificationCenter_ResultCount", Notifications.Count, NotificationHistoryQuery.MaximumLimit);

    public NotificationHistoryItemViewModel? SelectedNotification
    {
        get => _selectedNotification;
        set
        {
            if (!Set(ref _selectedNotification, value)) return;
            Raise(nameof(HasSelectedNotification));
        }
    }

    public AutomationRuleDefinition? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!Set(ref _selectedRule, value)) return;
            Raise(nameof(HasSelectedRule));
        }
    }

    public NotificationSourceOption? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    public NotificationSeverityOption? SelectedSeverity { get => _selectedSeverity; set => Set(ref _selectedSeverity, value); }
    public NotificationPeriodOption? SelectedPeriod { get => _selectedPeriod; set => Set(ref _selectedPeriod, value); }

    public async Task LoadAsync(int retentionDays)
    {
        _retentionDays = NotificationRetentionPolicy.Normalize(retentionDays);
        _loaded = true;
        await RefreshSourcesAsync();
        await Task.WhenAll(ReloadHistoryAsync(), LoadRulesAsync());
    }

    public async Task ReloadHistoryAsync()
    {
        var version = Interlocked.Increment(ref _queryVersion);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _queryCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        IsLoading = true;
        ErrorText = "";

        try
        {
            var records = await _store.QueryNotificationsAsync(BuildQuery(), cancellation.Token);
            if (version != Volatile.Read(ref _queryVersion)) return;
            ReplaceHistory(MergeWithLiveRecords(records));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载通知历史失败");
            if (version == Volatile.Read(ref _queryVersion)) ErrorText = _text["NotificationCenter_QueryFailed"];
        }
        finally
        {
            if (version == Volatile.Read(ref _queryVersion))
            {
                IsLoading = false;
                RaiseHistoryState();
            }
        }
    }

    public void ReplaySelectedNotification()
    {
        if (SelectedNotification is null) return;
        _desktopNotifications.Replay(SelectedNotification.Record);
    }

    public async Task DeleteSelectedNotificationAsync()
    {
        var selected = SelectedNotification;
        if (selected is null) return;

        await _operationGate.WaitAsync();
        try
        {
            _hiddenIds.Add(selected.Id);
            await _notifications.DeleteAsync(selected.Id);
            _liveRecords.Remove(selected.Id);
            SelectedNotification = null;
            await RefreshSourcesAsync();
            await ReloadHistoryAsync();
        }
        catch
        {
            _hiddenIds.Remove(selected.Id);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        var cutoff = DateTimeOffset.UtcNow;
        await _operationGate.WaitAsync();
        try
        {
            await _notifications.ClearThroughAsync(cutoff);
            _clearedThrough = cutoff;
            foreach (var id in _liveRecords.Values
                         .Where(record => record.CreatedAt <= cutoff)
                         .Select(record => record.Id)
                         .ToArray())
            {
                _liveRecords.Remove(id);
            }
            SelectedNotification = null;
            await RefreshSourcesAsync();
            await ReloadHistoryAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ApplyRetentionAsync(int retentionDays)
    {
        if (!NotificationRetentionPolicy.IsValid(retentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        _retentionDays = retentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        await _operationGate.WaitAsync();
        try
        {
            await _notifications.PruneAsync(cutoff);
            foreach (var id in _liveRecords.Values
                         .Where(record => record.CreatedAt < cutoff)
                         .Select(record => record.Id)
                         .ToArray())
            {
                _liveRecords.Remove(id);
            }
            await RefreshSourcesAsync();
            await ReloadHistoryAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SaveRuleAsync(AutomationRuleDefinition rule)
    {
        await _store.SaveRuleAsync(rule);
        await LoadRulesAsync();
    }

    public async Task DeleteSelectedRuleAsync()
    {
        if (SelectedRule is null) return;
        await _store.DeleteRuleAsync(SelectedRule.Id);
        SelectedRule = null;
        await LoadRulesAsync();
    }

    public async Task ShowTestNotificationAsync()
    {
        await _notifications.PublishAsync(new NotificationMessage
        {
            Origin = NotificationOrigin.Application,
            Source = "oh-my-pc",
            Title = "Oh My PC",
            Body = _text["Notification_TestBody"],
            Channels = NotificationChannels.Danmaku | NotificationChannels.Tray,
            Severity = NotificationSeverity.Info,
            SubjectKey = "test"
        });
    }

    public void RefreshLocalization()
    {
        var records = Notifications.Select(item => item.Record).ToArray();
        var rules = Rules.ToArray();
        BuildFilterOptions();
        ReplaceHistory(records);
        Replace(Rules, rules);
        Raise(nameof(HistoryCountText));
    }

    private async Task LoadRulesAsync() => Replace(Rules, await _store.ListRulesAsync());

    private NotificationHistoryQuery BuildQuery()
    {
        var now = DateTimeOffset.UtcNow;
        var retentionFrom = now.AddDays(-_retentionDays);
        DateTimeOffset? selectedFrom = SelectedPeriod?.Value switch
        {
            NotificationCenterPeriod.Today => new DateTimeOffset(DateTime.Today).ToUniversalTime(),
            NotificationCenterPeriod.LastSevenDays => now.AddDays(-7),
            _ => null
        };
        var createdFrom = selectedFrom is not null && selectedFrom > retentionFrom
            ? selectedFrom
            : retentionFrom;

        return new NotificationHistoryQuery
        {
            Source = SelectedSource?.Value,
            Severity = SelectedSeverity?.Value,
            CreatedFrom = createdFrom,
            Limit = NotificationHistoryQuery.MaximumLimit
        };
    }

    private IReadOnlyList<NotificationRecord> MergeWithLiveRecords(IEnumerable<NotificationRecord> records) =>
        records.Concat(_liveRecords.Values)
            .Where(MatchesCurrentFilter)
            .Where(record => !_hiddenIds.Contains(record.Id))
            .Where(record => _clearedThrough is null || record.CreatedAt > _clearedThrough.Value)
            .GroupBy(record => record.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.Id, StringComparer.Ordinal)
            .Take(NotificationHistoryQuery.MaximumLimit)
            .ToArray();

    private bool MatchesCurrentFilter(NotificationRecord record)
    {
        if (record.CreatedAt < DateTimeOffset.UtcNow.AddDays(-_retentionDays)) return false;
        if (SelectedSource?.Value is { } source && !string.Equals(record.Source, source, StringComparison.Ordinal)) return false;
        if (SelectedSeverity?.Value is { } severity && record.Severity != severity) return false;
        return SelectedPeriod?.Value switch
        {
            NotificationCenterPeriod.Today => record.CreatedAt >= new DateTimeOffset(DateTime.Today).ToUniversalTime(),
            NotificationCenterPeriod.LastSevenDays => record.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-7),
            _ => true
        };
    }

    private void DesktopNotifications_Published(object? sender, NotificationRecord record)
    {
        if (_clearedThrough is not null && record.CreatedAt <= _clearedThrough.Value) return;
        if (_hiddenIds.Contains(record.Id)) return;

        _liveRecords[record.Id] = record;
        if (_liveRecords.Count > NotificationHistoryQuery.MaximumLimit * 2)
        {
            foreach (var id in _liveRecords.Values
                         .OrderByDescending(item => item.CreatedAt)
                         .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                         .Skip(NotificationHistoryQuery.MaximumLimit)
                         .Select(item => item.Id)
                         .ToArray())
            {
                _liveRecords.Remove(id);
            }
        }

        EnsureSourceOption(record.Source);
        if (!_loaded || !MatchesCurrentFilter(record)) return;
        ReplaceHistory(MergeWithLiveRecords(Notifications.Select(item => item.Record)));
    }

    private async Task RefreshSourcesAsync()
    {
        var selectedValue = SelectedSource?.Value;
        var sources = (await _store.ListNotificationSourcesAsync())
            .Concat(_liveRecords.Values.Select(record => record.Source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(SourceOptions, [
            new NotificationSourceOption(null, _text["NotificationCenter_AllSources"]),
            .. sources.Select(source => new NotificationSourceOption(source, source))
        ]);
        SelectedSource = SourceOptions.FirstOrDefault(option => option.Value == selectedValue) ?? SourceOptions[0];
    }

    private void EnsureSourceOption(string source)
    {
        if (SourceOptions.Any(option => string.Equals(option.Value, source, StringComparison.Ordinal))) return;
        var selectedValue = SelectedSource?.Value;
        var sources = SourceOptions.Where(option => option.Value is not null).Select(option => option.Value!)
            .Append(source)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(SourceOptions, [
            new NotificationSourceOption(null, _text["NotificationCenter_AllSources"]),
            .. sources.Select(value => new NotificationSourceOption(value, value))
        ]);
        SelectedSource = SourceOptions.FirstOrDefault(option => option.Value == selectedValue) ?? SourceOptions[0];
    }

    private void BuildFilterOptions()
    {
        var sourceValue = SelectedSource?.Value;
        var severityValue = SelectedSeverity?.Value;
        var periodValue = SelectedPeriod?.Value ?? NotificationCenterPeriod.All;

        var sources = SourceOptions.Where(option => option.Value is not null).Select(option => option.Value!).ToArray();
        Replace(SourceOptions, [
            new NotificationSourceOption(null, _text["NotificationCenter_AllSources"]),
            .. sources.Select(source => new NotificationSourceOption(source, source))
        ]);
        Replace(SeverityOptions, [
            new NotificationSeverityOption(null, _text["NotificationCenter_AllSeverities"]),
            new NotificationSeverityOption(NotificationSeverity.Info, _text.GetEnum(NotificationSeverity.Info)),
            new NotificationSeverityOption(NotificationSeverity.Warning, _text.GetEnum(NotificationSeverity.Warning)),
            new NotificationSeverityOption(NotificationSeverity.Critical, _text.GetEnum(NotificationSeverity.Critical))
        ]);
        Replace(PeriodOptions, [
            new NotificationPeriodOption(NotificationCenterPeriod.All, _text["NotificationCenter_PeriodAll"]),
            new NotificationPeriodOption(NotificationCenterPeriod.Today, _text["NotificationCenter_PeriodToday"]),
            new NotificationPeriodOption(NotificationCenterPeriod.LastSevenDays, _text["NotificationCenter_PeriodSevenDays"])
        ]);

        SelectedSource = SourceOptions.FirstOrDefault(option => option.Value == sourceValue) ?? SourceOptions[0];
        SelectedSeverity = SeverityOptions.First(option => option.Value == severityValue);
        SelectedPeriod = PeriodOptions.First(option => option.Value == periodValue);
    }

    private void ReplaceHistory(IEnumerable<NotificationRecord> records)
    {
        var selectedId = SelectedNotification?.Id;
        Replace(Notifications, records.Select(record => new NotificationHistoryItemViewModel(record, _text)));
        SelectedNotification = Notifications.FirstOrDefault(item => item.Id == selectedId);
        RaiseHistoryState();
    }

    private void RaiseHistoryState()
    {
        Raise(nameof(IsHistoryEmpty));
        Raise(nameof(HistoryCountText));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    public void Dispose()
    {
        _desktopNotifications.Published -= DesktopNotifications_Published;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _operationGate.Dispose();
    }
}
