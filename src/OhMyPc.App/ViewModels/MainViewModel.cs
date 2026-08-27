using System.Collections.ObjectModel;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.InputStatus;
using OhMyPc.Infrastructure.LocalApi;
using OhMyPc.Infrastructure.LocalUsage;
using OhMyPc.Infrastructure.Providers;
using SkiaSharp;

namespace OhMyPc.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int ContributionWeekCount = 53;
    private const double ContributionCellStride = 16;
    private readonly IAppStore _store;
    private readonly LocalUsageRefreshService _localUsage;
    private readonly QuotaRefreshService _quotas;
    private readonly InputStatusRefreshService _inputStatus;
    private readonly EnvironmentSourceImporter _importer;
    private readonly LocalNotificationApiService _localApi;
    private readonly StartupRegistrationService _startup;
    private readonly LocalizationService _text;
    private readonly ILogger<MainViewModel> _logger;
    private bool _isBusy;
    private string _statusText;
    private string _lastUpdated;
    private long _todayTokens;
    private decimal _todayCost;
    private DataSourceDefinition? _selectedSource;
    private AppSettings _settings = new();
    private AppSettings _savedSettings = new();
    private bool _canEditSettings = true;
    private readonly Dictionary<DateOnly, UsageTrendPoint> _usageByDate = [];
    private readonly Dictionary<DateOnly, ContributionDayViewModel> _contributionByDate = [];
    private readonly Dictionary<DateOnly, int> _weekIndexes = [];
    private readonly ObservableCollection<FinancialPointI> _weeklyUsageValues = [];
    private readonly ObservableCollection<ObservableValue> _weeklyMessageValues = [];
    private readonly List<string> _weeklyLabels = [];
    private readonly SemaphoreSlim _usageRefreshGate = new(1, 1);
    private readonly CandlesticksSeries<FinancialPointI> _weeklyUsageSeries;
    private readonly ColumnSeries<ObservableValue> _weeklyMessageSeries;
    private DateOnly _usageStart;
    private DateOnly _usageEnd;
    private bool _usageInitialized;
    private string? _localizedChartLanguage;
    private string _contributionRangeText = "";
    private string _localApiStatusText = "";
    private UsageBreakdownGroup _breakdownGroup = UsageBreakdownGroup.Tool;
    private UsageBreakdownPeriod _breakdownPeriod = UsageBreakdownPeriod.Today;

    public MainViewModel(
        IAppStore store,
        LocalUsageRefreshService localUsage,
        QuotaRefreshService quotas,
        InputStatusRefreshService inputStatus,
        EnvironmentSourceImporter importer,
        VpnQuotaViewModel vpn,
        NotificationCenterViewModel notificationCenter,
        ProxyViewModel proxy,
        LocalNotificationApiService localApi,
        StartupRegistrationService startup,
        LocalizationService text,
        ILogger<MainViewModel> logger)
    {
        _store = store;
        _localUsage = localUsage;
        _quotas = quotas;
        _inputStatus = inputStatus;
        _importer = importer;
        Vpn = vpn;
        NotificationCenter = notificationCenter;
        Proxy = proxy;
        _localApi = localApi;
        _startup = startup;
        _text = text;
        _logger = logger;
        _statusText = text["Status_Starting"];
        _lastUpdated = text["Status_NotUpdated"];
        _weeklyUsageSeries = new CandlesticksSeries<FinancialPointI>
        {
            Values = _weeklyUsageValues,
            UpFill = new SolidColorPaint(new SKColor(240, 106, 106, 105)),
            UpStroke = new SolidColorPaint(new SKColor(240, 106, 106), 2),
            DownFill = new SolidColorPaint(new SKColor(83, 200, 146, 110)),
            DownStroke = new SolidColorPaint(new SKColor(83, 200, 146), 2),
            MaxBarWidth = 8
        };
        _weeklyMessageSeries = new ColumnSeries<ObservableValue>
        {
            Values = _weeklyMessageValues,
            Fill = new SolidColorPaint(new SKColor(99, 179, 237, 190)),
            Stroke = null,
            MaxBarWidth = 12
        };
        WeeklyUsageSeries = [_weeklyUsageSeries];
        WeeklyUsageXAxes = [CreateCategoryAxis(_weeklyLabels, 4, ContributionWeekCount)];
        WeeklyUsageYAxes = [CreateValueAxis()];
        WeeklyMessageSeries = [_weeklyMessageSeries];
        WeeklyMessageXAxes = [CreateCategoryAxis(_weeklyLabels, 4, ContributionWeekCount)];
        WeeklyMessageYAxes = [CreateValueAxis(startAtZero: true)];
        _localUsage.Refreshed += BackgroundUsageRefreshCompleted;
        _quotas.Refreshed += BackgroundQuotaRefreshCompleted;
        _inputStatus.Refreshed += BackgroundInputStatusRefreshCompleted;
        RefreshCommand = new AsyncCommand(RefreshAllAsync, () => !IsBusy);
        SelectBreakdownGroupCommand = new AsyncCommand<UsageBreakdownGroup>(SelectBreakdownGroupAsync);
        SelectBreakdownPeriodCommand = new AsyncCommand<UsageBreakdownPeriod>(SelectBreakdownPeriodAsync);
    }

    public ObservableCollection<QuotaRowViewModel> QuotaRows { get; } = [];
    public ObservableCollection<QuotaCardItemViewModel> QuotaCards { get; } = [];
    public ObservableCollection<DataSourceDefinition> Sources { get; } = [];
    public ObservableCollection<ContributionDayViewModel> ContributionDays { get; } = [];
    public ObservableCollection<ContributionMonthLabel> ContributionMonths { get; } = [];
    public ObservableCollection<UsageBreakdownRowViewModel> UsageBreakdownRows { get; } = [];
    public VpnQuotaViewModel Vpn { get; }
    public NotificationCenterViewModel NotificationCenter { get; }
    public ProxyViewModel Proxy { get; }

    public ICommand RefreshCommand { get; }
    public ICommand SelectBreakdownGroupCommand { get; }
    public ICommand SelectBreakdownPeriodCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) ((AsyncCommand)RefreshCommand).Refresh(); } }
    public bool CanEditSettings { get => _canEditSettings; private set => Set(ref _canEditSettings, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }
    public long TodayTokens { get => _todayTokens; private set { if (Set(ref _todayTokens, value)) Raise(nameof(TodayTokensText)); } }
    public decimal TodayCost { get => _todayCost; private set { if (Set(ref _todayCost, value)) Raise(nameof(TodayCostText)); } }
    public string TodayTokensText => TodayTokens.ToString("N0");
    public string TodayCostText => $"${TodayCost:0.00}";
    public DataSourceDefinition? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    public AppSettings Settings { get => _settings; private set => Set(ref _settings, value); }
    public string ContributionRangeText { get => _contributionRangeText; private set => Set(ref _contributionRangeText, value); }
    public string LocalApiStatusText { get => _localApiStatusText; private set => Set(ref _localApiStatusText, value); }
    public bool IsBreakdownByTool => _breakdownGroup == UsageBreakdownGroup.Tool;
    public bool IsBreakdownByModel => _breakdownGroup == UsageBreakdownGroup.Model;
    public bool IsBreakdownToday => _breakdownPeriod == UsageBreakdownPeriod.Today;
    public bool IsBreakdownMonth => _breakdownPeriod == UsageBreakdownPeriod.Month;
    public bool IsBreakdownAll => _breakdownPeriod == UsageBreakdownPeriod.All;
    public double ContributionGridWidth => ContributionWeekCount * ContributionCellStride - 4;
    public ISeries[] WeeklyUsageSeries { get; }
    public Axis[] WeeklyUsageXAxes { get; }
    public Axis[] WeeklyUsageYAxes { get; }
    public ISeries[] WeeklyMessageSeries { get; }
    public Axis[] WeeklyMessageXAxes { get; }
    public Axis[] WeeklyMessageYAxes { get; }
    public Margin WeeklyChartDrawMargin { get; } = new(82, Margin.Auto, 28, Margin.Auto);

    public async Task LoadAsync()
    {
        Settings = await _store.GetSettingsAsync();
        Settings.NotificationHistoryRetentionDays = NotificationRetentionPolicy.Normalize(Settings.NotificationHistoryRetentionDays);
        _savedSettings = SnapshotSettings(Settings);
        RefreshLocalApiStatus();
        await RefreshUsageAsync(fullHistory: true);
        await RefreshQuotaStateAsync();
        await Vpn.LoadAsync();
        await NotificationCenter.LoadAsync(Settings.NotificationHistoryRetentionDays);
        await Proxy.InitializeAsync();
        RefreshUsageLocalization();
        LastUpdated = _text.Format("Status_Updated", DateTime.Now);
    }

    public async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = _text["Status_Refreshing"];
        try
        {
            await _localUsage.RefreshAsync(fullHistory: false);
            await _quotas.RefreshAllAsync();
            await _inputStatus.RefreshAllAsync();
            await Vpn.RefreshAsync();
            await RefreshUsageAsync(fullHistory: false);
            await RefreshQuotaStateAsync();
            LastUpdated = _text.Format("Status_Updated", DateTime.Now);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> ImportEnvironmentAsync()
    {
        var count = await _importer.ImportAsync();
        await _quotas.RefreshAllAsync();
        await _inputStatus.RefreshAllAsync();
        await RefreshQuotaStateAsync();
        return count;
    }

    public async Task SaveSourceAsync(DataSourceDefinition source, string? apiKey)
    {
        var previous = await _store.GetDataSourceAsync(source.Id);
        if (previous is not null
            && (previous.Enabled != source.Enabled
                || !string.Equals(previous.ModelStatusUrl.Trim(), source.ModelStatusUrl.Trim(), StringComparison.Ordinal)))
        {
            _inputStatus.InvalidateSource(source.Id);
        }

        await _store.SaveDataSourceAsync(source, apiKey);
        await _quotas.RefreshAsync(source.Id);
        await _inputStatus.RefreshAsync(source.Id);
        await RefreshQuotaStateAsync();
    }

    public Task RefreshModelStatusAsync(string sourceId) => _inputStatus.RefreshAsync(sourceId);

    public async Task DeleteSelectedSourceAsync()
    {
        if (SelectedSource is null) return;
        var sourceId = SelectedSource.Id;
        _inputStatus.InvalidateSource(sourceId);
        await _store.DeleteDataSourceAsync(sourceId);
        SelectedSource = null;
        await RefreshQuotaStateAsync();
    }

    public async Task<string?> SaveSettingsAsync()
    {
        if (!CanEditSettings) return null;
        CanEditSettings = false;
        try
        {
            var candidate = SnapshotSettings(Settings);
            if (!NotificationRetentionPolicy.IsValid(candidate.NotificationHistoryRetentionDays))
            {
                return _text["Message_InvalidNotificationRetention"];
            }
            if (candidate.LocalApiEnabled && !LocalNotificationApiService.IsValidPort(candidate.LocalApiPort))
            {
                RefreshLocalApiStatus();
                return _text.Format(
                    "Message_InvalidLocalApiPort",
                    LocalNotificationApiService.MinimumPort,
                    LocalNotificationApiService.MaximumPort);
            }

            try
            {
                await _localApi.ApplySettingsAsync(candidate);
            }
            catch (LocalNotificationApiException exception)
            {
                _logger.LogWarning(exception, "本地弹幕 API 设置应用失败");
                RefreshLocalApiStatus();
                return _text.Format("Message_LocalApiStartFailed", candidate.LocalApiPort);
            }

            try
            {
                await _store.SaveSettingsAsync(candidate);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "应用设置保存失败");
                var previous = SnapshotSettings(_savedSettings);
                try
                {
                    await _localApi.ApplySettingsAsync(previous);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(rollbackException, "本地弹幕 API 设置回滚失败");
                }

                Settings = previous;
                RefreshLocalApiStatus();
                return _text["Message_SettingsSaveFailed"];
            }

            _savedSettings = SnapshotSettings(candidate);
            Settings = candidate;
            _text.Apply(candidate.Language);
            RefreshLocalApiStatus();
            _startup.Apply(candidate.StartWithWindows);
            RefreshUsageLocalization(force: true);
            Vpn.RefreshLocalization();
            NotificationCenter.RefreshLocalization();
            Proxy.RefreshLocalization();
            await RefreshQuotaStateAsync();
            try
            {
                await NotificationCenter.ApplyRetentionAsync(candidate.NotificationHistoryRetentionDays);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "保存设置后清理通知历史失败");
            }
            LastUpdated = _text["Status_SettingsSaved"];
            return null;
        }
        finally
        {
            CanEditSettings = true;
        }
    }

    private static AppSettings SnapshotSettings(AppSettings settings) => new()
    {
        Theme = settings.Theme,
        Language = settings.Language,
        StartWithWindows = settings.StartWithWindows,
        NotificationsEnabled = settings.NotificationsEnabled,
        DanmakuEnabled = settings.DanmakuEnabled,
        QuietHoursStart = settings.QuietHoursStart,
        QuietHoursEnd = settings.QuietHoursEnd,
        DanmakuOpacity = settings.DanmakuOpacity,
        DanmakuSpeed = settings.DanmakuSpeed,
        DanmakuFontSize = settings.DanmakuFontSize,
        DanmakuDurationSeconds = settings.DanmakuDurationSeconds,
        LocalApiEnabled = settings.LocalApiEnabled,
        LocalApiPort = settings.LocalApiPort,
        NotificationHistoryRetentionDays = settings.NotificationHistoryRetentionDays,
        CliProxyAutoStart = settings.CliProxyAutoStart
    };

    private void RefreshLocalApiStatus()
    {
        if (_localApi.IsRunning && _localApi.ActivePort is int activePort)
        {
            LocalApiStatusText = _text.Format("Status_LocalApiRunning", activePort);
        }
        else if (Settings.LocalApiEnabled && _localApi.LastError is not null)
        {
            LocalApiStatusText = _text["Status_LocalApiUnavailable"];
        }
        else
        {
            LocalApiStatusText = _text["Status_LocalApiStopped"];
        }
    }

    private async Task RefreshUsageAsync(bool fullHistory)
    {
        await _usageRefreshGate.WaitAsync();
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var rangeChanged = EnsureUsageRange(today);
            var from = fullHistory || rangeChanged ? _usageStart : today;
            var points = await _store.QueryUsageAsync(from, today);
            if (rangeChanged) RebuildUsage(points);
            else ApplyUsageSnapshot(points, from, today);
            await RefreshUsageBreakdownAsync();
            RefreshUsageLocalization();
        }
        finally
        {
            _usageRefreshGate.Release();
        }
    }

    private bool EnsureUsageRange(DateOnly today)
    {
        if (_usageInitialized && _usageEnd == today) return false;
        _usageEnd = today;
        _usageStart = StartOfWeek(today).AddDays(-(ContributionWeekCount - 1) * 7);
        _usageInitialized = true;
        _localizedChartLanguage = null;
        return true;
    }

    private void RebuildUsage(IReadOnlyList<UsageTrendPoint> points)
    {
        var incoming = points.ToDictionary(x => x.Date);
        _usageByDate.Clear();
        for (var date = _usageStart; date <= _usageEnd; date = date.AddDays(1))
        {
            _usageByDate[date] = incoming.GetValueOrDefault(date) ?? EmptyUsage(date);
        }

        ContributionDays.Clear();
        _contributionByDate.Clear();
        for (var date = _usageStart; date <= _usageEnd; date = date.AddDays(1))
        {
            var week = (date.DayNumber - _usageStart.DayNumber) / 7;
            var day = ((int)date.DayOfWeek + 6) % 7;
            var cell = new ContributionDayViewModel(date, week * ContributionCellStride, day * ContributionCellStride);
            cell.Update(_usageByDate[date], _text);
            ContributionDays.Add(cell);
            _contributionByDate[date] = cell;
        }

        _weeklyUsageValues.Clear();
        _weeklyMessageValues.Clear();
        _weekIndexes.Clear();
        for (var index = 0; index < ContributionWeekCount; index++)
        {
            var weekStart = _usageStart.AddDays(index * 7);
            _weekIndexes[weekStart] = index;
            _weeklyUsageValues.Add(BuildWeeklyCandle(weekStart));
            _weeklyMessageValues.Add(BuildWeeklyMessages(weekStart));
        }
        _weeklyLabels.Clear();
        _weeklyLabels.AddRange(_weekIndexes.OrderBy(x => x.Value).Select(x => x.Key.ToString("MM-dd")));

        UpdateContributionLevels();
        UpdateTodayUsage();
    }

    private void ApplyUsageSnapshot(IReadOnlyList<UsageTrendPoint> points, DateOnly from, DateOnly to)
    {
        var incoming = points.ToDictionary(x => x.Date);
        var changedDates = new List<DateOnly>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var next = incoming.GetValueOrDefault(date) ?? EmptyUsage(date);
            if (UsageEquals(_usageByDate[date], next)) continue;
            _usageByDate[date] = next;
            changedDates.Add(date);
        }

        if (changedDates.Count == 0) return;
        foreach (var date in changedDates)
        {
            var point = _usageByDate[date];
            _contributionByDate[date].Update(point, _text);
        }

        foreach (var weekStart in changedDates.Select(StartOfWeek).Distinct())
        {
            var weekIndex = _weekIndexes[weekStart];
            _weeklyUsageValues[weekIndex] = BuildWeeklyCandle(weekStart);
            _weeklyMessageValues[weekIndex].Value = BuildWeeklyMessages(weekStart).Value;
        }

        UpdateContributionLevels();
        UpdateTodayUsage();
    }

    private FinancialPointI BuildWeeklyCandle(DateOnly weekStart)
    {
        var activeDays = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .Where(date => date <= _usageEnd)
            .Select(date => _usageByDate[date])
            .Where(point => point.TotalTokens > 0)
            .ToArray();
        if (activeDays.Length == 0) return new FinancialPointI(0, 0, 0, 0);
        return new FinancialPointI(
            activeDays.Max(x => x.TotalTokens),
            activeDays[0].TotalTokens,
            activeDays[^1].TotalTokens,
            activeDays.Min(x => x.TotalTokens));
    }

    private ObservableValue BuildWeeklyMessages(DateOnly weekStart)
    {
        var messages = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .Where(date => date <= _usageEnd)
            .Sum(date => _usageByDate[date].MessageCount);
        return new ObservableValue(messages);
    }

    private void UpdateContributionLevels()
    {
        var maximum = _usageByDate.Values.Max(x => x.TotalTokens);
        foreach (var cell in ContributionDays)
        {
            var ratio = maximum == 0 ? 0 : (double)cell.Tokens / maximum;
            cell.SetLevel(ratio switch
            {
                0 => 0,
                <= 0.1 => 1,
                <= 0.35 => 2,
                <= 0.7 => 3,
                _ => 4
            });
        }
    }

    private void UpdateTodayUsage()
    {
        var today = _usageByDate[_usageEnd];
        TodayTokens = today.TotalTokens;
        TodayCost = today.CostUsd;
    }

    private async Task SelectBreakdownGroupAsync(UsageBreakdownGroup group)
    {
        if (_breakdownGroup == group) return;
        _breakdownGroup = group;
        Raise(nameof(IsBreakdownByTool));
        Raise(nameof(IsBreakdownByModel));
        await RefreshUsageBreakdownAsync();
    }

    private async Task SelectBreakdownPeriodAsync(UsageBreakdownPeriod period)
    {
        if (_breakdownPeriod == period) return;
        _breakdownPeriod = period;
        Raise(nameof(IsBreakdownToday));
        Raise(nameof(IsBreakdownMonth));
        Raise(nameof(IsBreakdownAll));
        await RefreshUsageBreakdownAsync();
    }

    private async Task RefreshUsageBreakdownAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = _breakdownPeriod switch
        {
            UsageBreakdownPeriod.Today => today,
            UsageBreakdownPeriod.Month => new DateOnly(today.Year, today.Month, 1),
            _ => DateOnly.MinValue
        };
        var points = await _store.QueryUsageBreakdownAsync(from, today, _breakdownGroup);
        var maximum = points.Count == 0 ? 0 : points.Max(x => x.TotalTokens);
        Replace(UsageBreakdownRows, points.Select(point => new UsageBreakdownRowViewModel(
            point,
            maximum == 0 ? 0 : (double)point.TotalTokens / maximum * 100)));
    }

    private void RefreshUsageLocalization(bool force = false)
    {
        if (!_usageInitialized || (!force && _localizedChartLanguage == _text.CurrentLanguage)) return;
        _localizedChartLanguage = _text.CurrentLanguage;
        ContributionRangeText = _text.Format("Overview_DateRange", _usageStart, _usageEnd);
        ContributionMonths.Clear();
        var previousMonth = -1;
        for (var week = 0; week < ContributionWeekCount; week++)
        {
            var date = _usageStart.AddDays(week * 7);
            if (date.Month == previousMonth) continue;
            ContributionMonths.Add(new ContributionMonthLabel(date.ToString("MMM"), week * ContributionCellStride));
            previousMonth = date.Month;
        }
        foreach (var cell in ContributionDays) cell.RefreshText(_text);
        foreach (var row in UsageBreakdownRows) row.RefreshText();
        _weeklyUsageSeries.Name = _text["Overview_WeeklyCandles"];
        _weeklyMessageSeries.Name = _text["Overview_WeeklyMessages"];
    }

    private async Task RefreshQuotaStateAsync()
    {
        var quotas = await _store.ListCurrentQuotasAsync();
        var sources = await _store.ListDataSourcesAsync();
        Replace(QuotaRows, quotas.Select(x => new QuotaRowViewModel(x, _text)));
        Replace(Sources, sources);
        var quotaRowsBySource = quotas
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Select(x => new QuotaRowViewModel(x, _text)).ToList());
        var cards = sources
            .Select(source => (QuotaCardItemViewModel)new QuotaSourceCardViewModel(
                source,
                quotaRowsBySource.GetValueOrDefault(source.Id) ?? [],
                _inputStatus.GetSnapshot(source.Id),
                _text))
            .Append(new AddQuotaSourceCardViewModel());
        Replace(QuotaCards, cards);
        StatusText = sources.Any(x => x.Status is ProviderStatus.Unavailable or ProviderStatus.AuthenticationFailed)
            ? _text["Status_AttentionRequired"]
            : _text["Status_AllNominal"];
    }

    private void BackgroundUsageRefreshCompleted(object? sender, LocalUsageRefreshedEventArgs e)
    {
        if (IsBusy) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => RefreshUsageFromBackgroundAsync(e.FullHistory)).Task.Unwrap();
    }

    private async Task RefreshUsageFromBackgroundAsync(bool fullHistory)
    {
        await RefreshUsageAsync(fullHistory);
        LastUpdated = _text.Format("Status_Updated", DateTime.Now);
    }

    private void BackgroundInputStatusRefreshCompleted(object? sender, EventArgs e)
    {
        if (IsBusy) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshQuotasFromBackgroundAsync).Task.Unwrap();
    }

    private void BackgroundQuotaRefreshCompleted(object? sender, EventArgs e)
    {
        if (IsBusy) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshQuotasFromBackgroundAsync).Task.Unwrap();
    }

    private async Task RefreshQuotasFromBackgroundAsync()
    {
        await RefreshQuotaStateAsync();
        LastUpdated = _text.Format("Status_Updated", DateTime.Now);
    }

    private static Axis CreateCategoryAxis(IReadOnlyList<string> labels, double minimumStep, int pointCount) => new()
    {
        UnitWidth = 1,
        MinStep = minimumStep,
        ForceStepToMin = true,
        MinLimit = -0.5,
        MaxLimit = pointCount - 0.5,
        Labeler = value =>
        {
            var index = (int)Math.Round(value);
            return index >= 0 && index < labels.Count ? labels[index] : "";
        },
        LabelsPaint = new SolidColorPaint(new SKColor(154, 164, 159)),
        SeparatorsPaint = new SolidColorPaint(new SKColor(54, 60, 56), 1),
        TextSize = 11
    };

    private static Axis CreateValueAxis(bool startAtZero = false)
    {
        var axis = new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(154, 164, 159)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(54, 60, 56), 1),
            TextSize = 11,
            Labeler = CompactNumber
        };
        if (startAtZero) axis.MinLimit = 0;
        return axis;
    }

    private static string CompactNumber(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000:0.#}M",
        >= 1_000 => $"{value / 1_000:0.#}K",
        _ => value.ToString("0")
    };

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static UsageTrendPoint EmptyUsage(DateOnly date) => new() { Date = date };

    private static bool UsageEquals(UsageTrendPoint left, UsageTrendPoint right) =>
        left.InputTokens == right.InputTokens
        && left.OutputTokens == right.OutputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.MessageCount == right.MessageCount
        && left.ActiveTimeMs == right.ActiveTimeMs
        && left.CostUsd == right.CostUsd;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
