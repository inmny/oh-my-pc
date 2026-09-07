using System.Globalization;

namespace OhMyPc.Core;

/// <summary>
/// 一周的用量 K 线摘要：除金融四元组外携带 tooltip 所需的聚合信息。
/// 峰值/最低给出具体日期，环比给出与上周总量的比值；首周无上周数据时 Trend 为 null。
/// </summary>
public sealed record WeeklyUsageSummary(
    DateOnly WeekStart,
    long TotalTokens,
    decimal CostUsd,
    long MessageCount,
    long ActiveDays,
    DateOnly? PeakDate,
    long PeakTokens,
    DateOnly? TroughDate,
    long TroughTokens,
    long? PreviousTotalTokens)
{
    /// <summary>环比百分比（相对上周），上周无数据时为 null。</summary>
    public double? TrendPercent => PreviousTotalTokens is > 0
        ? Math.Round((TotalTokens - PreviousTotalTokens.Value) * 100.0 / PreviousTotalTokens.Value, 0)
        : null;

    /// <summary>活跃日的日均用量；无活跃日时为 0。</summary>
    public long DailyAverage => ActiveDays == 0 ? 0 : TotalTokens / ActiveDays;

    public string WeekRangeText => $"{WeekStart:MM-dd} ~ {WeekStart.AddDays(6):MM-dd}";
}

/// <summary>WeeklyUsageSummary 的构建与周序列计算。</summary>
public static class WeeklyUsageSummaries
{
    /// <summary>按周构建摘要序列；首周（没有任何前一周数据）的环比为 null。</summary>
    public static IReadOnlyList<WeeklyUsageSummary> Build(
        DateOnly usageStart,
        DateOnly usageEnd,
        IReadOnlyDictionary<DateOnly, (long TotalTokens, decimal CostUsd, long MessageCount)> daily)
    {
        var result = new List<WeeklyUsageSummary>();
        var firstWeek = StartOfWeek(usageStart);
        var lastWeek = StartOfWeek(usageEnd);
        long? previousTotal = null;
        for (var weekStart = firstWeek; weekStart <= lastWeek; weekStart = weekStart.AddDays(7))
        {
            long total = 0, messages = 0, activeDays = 0;
            decimal cost = 0;
            DateOnly? peakDate = null, troughDate = null;
            long peak = 0, trough = 0;
            for (
            var day = weekStart;
            day < weekStart.AddDays(7);
            day = day.AddDays(1))
            {
                if (day < usageStart || day > usageEnd) continue;
                if (!daily.TryGetValue(day, out var point)) continue;
                total += point.TotalTokens;
                cost += point.CostUsd;
                messages += point.MessageCount;
                if (point.TotalTokens <= 0) continue;
                activeDays++;
                if (point.TotalTokens > peak)
                {
                    peak = point.TotalTokens;
                    peakDate = day;
                }
                if (troughDate is null || point.TotalTokens < trough)
                {
                    trough = point.TotalTokens;
                    troughDate = day;
                }
            }
            result.Add(new WeeklyUsageSummary(
                weekStart, total, cost, messages, activeDays,
                peakDate, peak, troughDate, trough, previousTotal));
            previousTotal = total;
        }
        return result;
    }

    /// <summary>一周的起始日（周一）。</summary>
    public static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
