using System.Collections.ObjectModel;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Vpn;
using SkiaSharp;

namespace OhMyPc.App.ViewModels;

public sealed class VpnQuotaViewModel : ViewModelBase
{
    private const double BytesPerGibibyte = 1024d * 1024d * 1024d;
    private const int HistoryDayCount = 30;
    private readonly IAppStore _store;
    private readonly IVpnQuotaClient _client;
    private readonly VpnQuotaRefreshService _refreshService;
    private readonly LocalizationService _text;
    private readonly ObservableCollection<ObservableValue> _dailyUsageValues = [];
    private readonly List<string> _dailyUsageLabels = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private VpnAccountDefinition? _account;
    private IReadOnlyList<VpnDailyUsagePoint> _dailyHistory = [];
    private double _averageDailyBytes;
    private DateTime? _estimatedExhaustion;
    private bool _hasHistory;
    private DateOnly _historyFrom;
    private DateOnly _historyTo;
    private bool _isBusy;
    private readonly ColumnSeries<ObservableValue> _dailyUsageSeries;

    public VpnQuotaViewModel(
        IAppStore store,
        IVpnQuotaClient client,
        VpnQuotaRefreshService refreshService,
        LocalizationService text)
    {
        _store = store;
        _client = client;
        _refreshService = refreshService;
        _text = text;
        _dailyUsageSeries = new ColumnSeries<ObservableValue>
        {
            Name = _text["Vpn_DailyUsageSeries"],
            Values = _dailyUsageValues,
            Fill = new SolidColorPaint(new SKColor(83, 200, 146, 205)),
            Stroke = null,
            MaxBarWidth = 18
        };
        DailyUsageSeries = [_dailyUsageSeries];
        DailyUsageXAxes = [CreateDateAxis(_dailyUsageLabels)];
        DailyUsageYAxes = [CreateValueAxis()];
        _refreshService.Refreshed += BackgroundRefreshCompleted;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => HasAccount && !IsBusy);
    }

    public ICommand RefreshCommand { get; }
    public ISeries[] DailyUsageSeries { get; }
    public Axis[] DailyUsageXAxes { get; }
    public Axis[] DailyUsageYAxes { get; }
    public Margin DailyUsageDrawMargin { get; } = new(58, Margin.Auto, 24, Margin.Auto);
    public bool HasAccount => _account is not null;
    public bool IsEmpty => !HasAccount;
    public bool HasError => !string.IsNullOrWhiteSpace(_account?.LastError);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ((AsyncCommand)RefreshCommand).Refresh();
        }
    }
    public string Email => _account?.Email ?? "";
    public string PlanName => string.IsNullOrWhiteSpace(_account?.PlanName) ? _text["Vpn_NoPlan"] : _account.PlanName;
    public string StatusText => _account is null ? "" : _text.GetEnum(_account.Status);
    public string RemainingText => FormatBytes(_account?.RemainingBytes ?? 0);
    public string RemainingPercentText => _text.Format("Vpn_RemainingPercent", _account?.RemainingPercent ?? 0);
    public double RemainingPercent => _account?.RemainingPercent ?? 0;
    public string TotalText => FormatBytes(_account?.TransferLimitBytes ?? 0);
    public string UsedText => FormatBytes(_account?.UsedBytes ?? 0);
    public string UploadedText => FormatBytes(_account?.UploadedBytes ?? 0);
    public string DownloadedText => FormatBytes(_account?.DownloadedBytes ?? 0);
    public string ExpiresText => _account?.ExpiresAt is null
        ? _text["Vpn_Permanent"]
        : _account.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string DaysRemainingText
    {
        get
        {
            if (_account?.ExpiresAt is null) return _text["Vpn_Permanent"];
            var days = (int)Math.Ceiling((_account.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalDays);
            return days <= 0 ? _text["Vpn_Expired"] : _text.Format("Vpn_DaysRemaining", days);
        }
    }
    public string ResetDayText => _account?.ResetDay is null
        ? _text["Vpn_NoResetDay"]
        : _text.Format("Vpn_ResetDayValue", _account.ResetDay.Value);
    public string LastUpdatedText => _account?.LastSuccessAt is null
        ? _text["Status_NotUpdated"]
        : _text.Format("Status_Updated", _account.LastSuccessAt.Value.ToLocalTime());
    public string ErrorText => _account?.LastError ?? "";
    public bool HasHistory => _hasHistory;
    public string HistoryRangeText => _dailyHistory.Count == 0
        ? _text["Vpn_NoHistory"]
        : _text.Format("Vpn_HistoryRange", _historyFrom, _historyTo);
    public string AverageDailyText => _averageDailyBytes <= 0
        ? _text["Vpn_NotEnoughHistory"]
        : _text.Format("Vpn_PerDay", FormatBytes((long)Math.Round(_averageDailyBytes)));
    public string EstimatedExhaustionText
    {
        get
        {
            if (_account?.RemainingBytes <= 0) return _text["Vpn_Depleted"];
            return _estimatedExhaustion is null
                ? _text["Vpn_NotEnoughHistory"]
                : _estimatedExhaustion.Value.ToString("yyyy-MM-dd");
        }
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            _account = await _store.GetVpnAccountAsync();
            await LoadHistoryAsync();
            RaiseAll();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task ConnectAsync(string email, string password)
    {
        IsBusy = true;
        try
        {
            var authData = await _client.LoginAsync(email, password);
            var account = _account;
            if (account is not null && !string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                await _store.DeleteVpnAccountAsync();
                account = null;
            }
            account ??= new VpnAccountDefinition();
            account.Email = email;
            account.Status = ProviderStatus.Unknown;
            account.LastError = null;
            await _store.SaveVpnAccountAsync(account, authData);
            await _refreshService.RefreshAsync();
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (!HasAccount || IsBusy) return;
        IsBusy = true;
        try
        {
            await _refreshService.RefreshAsync();
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RemoveAsync()
    {
        await _store.DeleteVpnAccountAsync();
        _account = null;
        _dailyHistory = [];
        _dailyUsageValues.Clear();
        _dailyUsageLabels.Clear();
        _averageDailyBytes = 0;
        _estimatedExhaustion = null;
        _hasHistory = false;
        RaiseAll();
    }

    public void RefreshLocalization()
    {
        _dailyUsageSeries.Name = _text["Vpn_DailyUsageSeries"];
        RaiseAll();
    }

    private void BackgroundRefreshCompleted(object? sender, EventArgs e)
    {
        if (IsBusy) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(LoadAsync).Task.Unwrap();
    }

    private string FormatBytes(long bytes) => _text.Format("Vpn_Gibibytes", bytes / BytesPerGibibyte);

    private async Task LoadHistoryAsync()
    {
        _dailyUsageValues.Clear();
        _dailyUsageLabels.Clear();
        _dailyHistory = [];
        _averageDailyBytes = 0;
        _estimatedExhaustion = null;
        _hasHistory = false;
        _historyTo = DateOnly.FromDateTime(DateTime.Now);
        _historyFrom = _historyTo.AddDays(1 - HistoryDayCount);
        if (_account is null) return;

        _dailyHistory = await _store.QueryVpnDailyUsageAsync(_historyFrom.AddDays(-1), _historyTo);
        var trend = VpnDailyUsageTrendCalculator.Calculate(_dailyHistory, _historyFrom, _historyTo);
        for (var date = _historyFrom; date <= _historyTo; date = date.AddDays(1))
        {
            _dailyUsageLabels.Add(date.ToString("MM-dd"));
            _dailyUsageValues.Add(new ObservableValue(
                trend.DailyBytes.TryGetValue(date, out var bytes)
                    ? bytes / BytesPerGibibyte
                    : null));
        }

        _hasHistory = trend.DailyBytes.Count > 0;
        _averageDailyBytes = trend.AverageDailyBytes;
        if (_averageDailyBytes > 0 && _account.RemainingBytes > 0)
        {
            var days = (int)Math.Ceiling(_account.RemainingBytes / _averageDailyBytes);
            _estimatedExhaustion = DateTime.Today.AddDays(days);
        }
    }

    private static Axis CreateDateAxis(IReadOnlyList<string> labels) => new()
    {
        UnitWidth = 1,
        MinStep = 5,
        ForceStepToMin = true,
        MinLimit = -0.5,
        MaxLimit = HistoryDayCount - 0.5,
        Labeler = value =>
        {
            var index = (int)Math.Round(value);
            return index >= 0 && index < labels.Count ? labels[index] : "";
        },
        LabelsPaint = new SolidColorPaint(new SKColor(154, 164, 159)),
        SeparatorsPaint = new SolidColorPaint(new SKColor(54, 60, 56), 1),
        TextSize = 11
    };

    private static Axis CreateValueAxis() => new()
    {
        MinLimit = 0,
        Labeler = value => $"{value:0.#} GiB",
        LabelsPaint = new SolidColorPaint(new SKColor(154, 164, 159)),
        SeparatorsPaint = new SolidColorPaint(new SKColor(54, 60, 56), 1),
        TextSize = 11
    };

    private void RaiseAll()
    {
        Raise(nameof(HasAccount));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasError));
        Raise(nameof(Email));
        Raise(nameof(PlanName));
        Raise(nameof(StatusText));
        Raise(nameof(RemainingText));
        Raise(nameof(RemainingPercentText));
        Raise(nameof(RemainingPercent));
        Raise(nameof(TotalText));
        Raise(nameof(UsedText));
        Raise(nameof(UploadedText));
        Raise(nameof(DownloadedText));
        Raise(nameof(ExpiresText));
        Raise(nameof(DaysRemainingText));
        Raise(nameof(ResetDayText));
        Raise(nameof(LastUpdatedText));
        Raise(nameof(ErrorText));
        Raise(nameof(HasHistory));
        Raise(nameof(HistoryRangeText));
        Raise(nameof(AverageDailyText));
        Raise(nameof(EstimatedExhaustionText));
        ((AsyncCommand)RefreshCommand).Refresh();
    }
}
