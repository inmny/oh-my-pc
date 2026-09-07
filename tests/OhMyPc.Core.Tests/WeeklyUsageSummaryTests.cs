using OhMyPc.Core;
using Xunit;

namespace OhMyPc.Core.Tests;

public sealed class WeeklyUsageSummaryTests
{
    private static readonly DateOnly Monday = new(2026, 8, 31); // 周一

    [Fact]
    public void Build_AggregatesPeakTroughAndTotals()
    {
        // 周三峰值 3000，周五谷值 100；周日超出 usageEnd 不计入
        var daily = new Dictionary<DateOnly, (long, decimal, long)>
        {
            [Monday] = (1_000, 1m, 10),
            [Monday.AddDays(2)] = (3_000, 3m, 30),
            [Monday.AddDays(4)] = (100, 0.1m, 1)
        };

        var summaries = WeeklyUsageSummaries.Build(Monday, Monday.AddDays(4), daily);

        var summary = Assert.Single(summaries);
        Assert.Equal(4_100, summary.TotalTokens);
        Assert.Equal(4.1m, summary.CostUsd);
        Assert.Equal(41, summary.MessageCount);
        Assert.Equal(3, summary.ActiveDays);
        Assert.Equal(Monday.AddDays(2), summary.PeakDate);
        Assert.Equal(3_000, summary.PeakTokens);
        Assert.Equal(Monday.AddDays(4), summary.TroughDate);
        Assert.Equal(100, summary.TroughTokens);
        Assert.Equal(4_100 / 3, summary.DailyAverage);
        // 首周无上周数据
        Assert.Null(summary.PreviousTotalTokens);
        Assert.Null(summary.TrendPercent);
        Assert.Equal("08-31 ~ 09-06", summary.WeekRangeText);
    }

    [Fact]
    public void Build_ComputesTrendAgainstPreviousWeek()
    {
        var daily = new Dictionary<DateOnly, (long, decimal, long)>
        {
            [Monday] = (1_000, 0m, 0),
            [Monday.AddDays(7)] = (1_250, 0m, 0)
        };

        var summaries = WeeklyUsageSummaries.Build(Monday, Monday.AddDays(7), daily);

        Assert.Equal(2, summaries.Count);
        Assert.Null(summaries[0].TrendPercent);
        Assert.Equal(1_000, summaries[1].PreviousTotalTokens);
        Assert.Equal(25, summaries[1].TrendPercent);
    }

    [Fact]
    public void Build_SkipsEmptyDaysForPeakAndTrough()
    {
        // 周二周三无数据（0），峰值谷值只应在有数据的日子取
        var daily = new Dictionary<DateOnly, (long, decimal, long)>
        {
            [Monday] = (500, 0m, 0),
            [Monday.AddDays(5)] = (700, 0m, 0)
        };

        var summaries = WeeklyUsageSummaries.Build(Monday, Monday.AddDays(6), daily);

        var summary = summaries[0];
        Assert.Equal(1_200, summary.TotalTokens);
        Assert.Equal(2, summary.ActiveDays);
        Assert.Equal(Monday, summary.TroughDate);
        Assert.Equal(Monday.AddDays(5), summary.PeakDate);
    }

    [Fact]
    public void Build_ZeroTrendReturnsNeutral()
    {
        var daily = new Dictionary<DateOnly, (long, decimal, long)>
        {
            [Monday] = (500, 0m, 0),
            [Monday.AddDays(7)] = (500, 0m, 0)
        };

        var summaries = WeeklyUsageSummaries.Build(Monday, Monday.AddDays(7), daily);

        Assert.Equal(0, summaries[1].TrendPercent);
    }
}
