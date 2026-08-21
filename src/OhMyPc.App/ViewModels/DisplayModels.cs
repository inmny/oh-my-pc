using OhMyPc.Core.Domain;
using OhMyPc.App.Services;
using OhMyPc.Infrastructure.InputStatus;

namespace OhMyPc.App.ViewModels;

public enum UsageBreakdownPeriod
{
    Today,
    Month,
    All
}

public sealed class QuotaRowViewModel(QuotaSnapshot snapshot, LocalizationService text)
{
    public string SourceId => snapshot.SourceId;
    public string SourceName => snapshot.SourceName;
    public string WindowKey => snapshot.WindowKey;
    public string Label => text.GetQuotaLabel(snapshot);
    public double RemainingPercent => snapshot.RemainingPercent ?? 0;
    public bool HasPercentage => snapshot.RemainingPercent is not null;
    private string Unit => snapshot.WindowKey switch
    {
        "zhipu-mcp-monthly" => text["Quota_CallsUnit"],
        _ => snapshot.Unit
    };
    public string RemainingText => snapshot.Remaining is null
        ? text["Quota_NoFixedLimit"]
        : $"{snapshot.Remaining:0.##} {Unit}";
    private double? DisplayLimit => snapshot.ProgressLimit ?? snapshot.Limit;
    private double DisplayUsed => snapshot.ProgressLimit is not null && snapshot.Remaining is not null
        ? Math.Max(0, snapshot.ProgressLimit.Value - snapshot.Remaining.Value)
        : snapshot.Used;
    public string UsedText => DisplayLimit is null
        ? text.Format("Quota_Used", DisplayUsed, Unit)
        : text.Format("Quota_UsedOfLimit", DisplayUsed, DisplayLimit, Unit);
    public string ResetText => snapshot.ResetAt is null ? text["Quota_NoResetTime"] : text.Format("Quota_ResetsAt", snapshot.ResetAt.Value.ToLocalTime());
    public string StatusText => text.GetEnum(snapshot.Status);
    public string PercentText => snapshot.RemainingPercent is null ? "--" : $"{snapshot.RemainingPercent:0.#}%";
}

public abstract class QuotaCardItemViewModel;

public sealed class QuotaSourceCardViewModel(
    DataSourceDefinition source,
    IEnumerable<QuotaRowViewModel> quotas,
    InputSourceStatusSnapshot? modelStatus,
    LocalizationService text) : QuotaCardItemViewModel
{
    public DataSourceDefinition Source { get; } = source;
    public IReadOnlyList<QuotaRowViewModel> Quotas { get; } = quotas.ToList();
    public IReadOnlyList<ModelStatusRowViewModel> ModelStatuses { get; } =
        (modelStatus?.Models ?? []).Select(model => new ModelStatusRowViewModel(model, text)).ToArray();
    public string SourceId => Source.Id;
    public string SourceName => Source.Name;
    public string ProviderTypeText => text.GetEnum(Source.Kind);
    public string StatusText => text.GetEnum(Source.Status);
    public string EndpointText => Source.BaseUrl;
    public bool HasQuotas => Quotas.Count > 0;
    public bool HasModelStatusEndpoint => Source.Enabled && !string.IsNullOrWhiteSpace(Source.ModelStatusUrl);
    public bool HasModelStatuses => ModelStatuses.Count > 0;
    public bool HasModelStatusError => !string.IsNullOrWhiteSpace(modelStatus?.Error);
    public bool ShowModelStatusWaiting => !HasModelStatuses && !HasModelStatusError;
    public string ModelStatusError => modelStatus?.Error ?? "";
    public string ModelStatusUpdatedText => modelStatus?.LastSuccessAt is null
        ? text["Status_NotUpdated"]
        : text.Format("Status_Updated", modelStatus.LastSuccessAt.Value.ToLocalTime());
}

public sealed class ModelStatusRowViewModel(InputModelStatusSnapshot status, LocalizationService text)
{
    private const int SampleCapacity = 20;
    private readonly IReadOnlyList<bool> _knownSamples = status.Samples;

    public string Model => status.Model;
    public bool IsAvailable => status.Available;
    public string StatusText => text[status.Available ? "ModelStatus_Online" : "ModelStatus_Unavailable"];
    public string LatencyText => status.LatencyMilliseconds is null
        ? "--"
        : text.Format("ModelStatus_Latency", status.LatencyMilliseconds.Value);
    public string SummaryText => text.Format(
        "ModelStatus_UptimeSummary",
        _knownSamples.Count == 0 ? 0 : _knownSamples.Count(sample => sample) * 100d / _knownSamples.Count,
        _knownSamples.Count);
    public string ErrorText => status.Error ?? "";
    public IReadOnlyList<ModelStatusSampleViewModel> Samples { get; } =
        Enumerable.Repeat<bool?>(null, Math.Max(0, SampleCapacity - status.Samples.Count))
            .Concat(status.Samples.Select(sample => (bool?)sample))
            .TakeLast(SampleCapacity)
            .Select(sample => new ModelStatusSampleViewModel(sample))
            .ToArray();
}

public sealed record ModelStatusSampleViewModel(bool? Available);

public sealed class AddQuotaSourceCardViewModel : QuotaCardItemViewModel;

public sealed class ContributionDayViewModel(DateOnly date, double left, double top) : ViewModelBase
{
    private long _tokens;
    private long _messages;
    private int _level;
    private string _tooltip = "";

    public DateOnly Date { get; } = date;
    public double Left { get; } = left;
    public double Top { get; } = top;
    public long Tokens { get => _tokens; private set => Set(ref _tokens, value); }
    public long Messages { get => _messages; private set => Set(ref _messages, value); }
    public int Level { get => _level; private set => Set(ref _level, value); }
    public string Tooltip { get => _tooltip; private set => Set(ref _tooltip, value); }

    public void Update(UsageTrendPoint point, LocalizationService text)
    {
        Tokens = point.TotalTokens;
        Messages = point.MessageCount;
        RefreshText(text);
    }

    public void SetLevel(int level) => Level = level;

    public void RefreshText(LocalizationService text) =>
        Tooltip = text.Format("Overview_DayTooltip", Date, Tokens, Messages);
}

public sealed record ContributionMonthLabel(string Text, double Left);

public sealed class UsageBreakdownRowViewModel(UsageBreakdownPoint point, double relativePercent) : ViewModelBase
{
    public string Name => point.Name;
    public long TotalTokens => point.TotalTokens;
    public long InputTokens => point.InputTokens + point.CacheReadTokens + point.CacheWriteTokens;
    public long OutputTokens => point.OutputTokens;
    public long CacheHitTokens => point.CacheReadTokens;
    public double CacheHitPercent => InputTokens == 0 ? 0 : (double)CacheHitTokens / InputTokens * 100;
    public double RelativePercent { get; } = relativePercent;
    public string TotalTokensText => TotalTokens.ToString("N0");
    public string InputTokensText => InputTokens.ToString("N0");
    public string OutputTokensText => OutputTokens.ToString("N0");
    public string CacheHitTokensText => CacheHitTokens.ToString("N0");
    public string CacheHitPercentText => $"{CacheHitPercent:0.#}%";
    public string CostText => $"${point.CostUsd:0.00}";

    public void RefreshText()
    {
        Raise(nameof(TotalTokensText));
        Raise(nameof(InputTokensText));
        Raise(nameof(OutputTokensText));
        Raise(nameof(CacheHitTokensText));
        Raise(nameof(CacheHitPercentText));
        Raise(nameof(CostText));
    }
}
