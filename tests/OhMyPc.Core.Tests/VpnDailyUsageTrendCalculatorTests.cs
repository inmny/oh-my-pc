using OhMyPc.Core.Domain;

namespace OhMyPc.Core.Tests;

public sealed class VpnDailyUsageTrendCalculatorTests
{
    [Fact]
    public void Calculate_UsesBaselineAndDistributesMultiDayIntervals()
    {
        var from = new DateOnly(2026, 8, 2);
        var trend = VpnDailyUsageTrendCalculator.Calculate(
            [
                Usage(from.AddDays(-1), 100),
                Usage(from, 200),
                Usage(from.AddDays(2), 500)
            ],
            from,
            from.AddDays(2));

        Assert.Equal(100, trend.DailyBytes[from]);
        Assert.Equal(150, trend.DailyBytes[from.AddDays(1)]);
        Assert.Equal(150, trend.DailyBytes[from.AddDays(2)]);
        Assert.Equal(400d / 3d, trend.AverageDailyBytes, precision: 6);
    }

    [Fact]
    public void Calculate_UsesCurrentCounterAfterSingleDayReset()
    {
        var from = new DateOnly(2026, 8, 1);
        var trend = VpnDailyUsageTrendCalculator.Calculate(
            [Usage(from, 1_000), Usage(from.AddDays(1), 200)],
            from,
            from.AddDays(1));

        Assert.Equal(200, trend.DailyBytes[from.AddDays(1)]);
        Assert.Equal(200, trend.AverageDailyBytes);
    }

    [Fact]
    public void Calculate_SkipsAmbiguousMultiDayResetInterval()
    {
        var from = new DateOnly(2026, 8, 1);
        var trend = VpnDailyUsageTrendCalculator.Calculate(
            [Usage(from, 1_000), Usage(from.AddDays(2), 200)],
            from,
            from.AddDays(2));

        Assert.Empty(trend.DailyBytes);
        Assert.Equal(0, trend.AverageDailyBytes);
    }

    private static VpnDailyUsagePoint Usage(DateOnly date, long usedBytes) => new()
    {
        Date = date,
        DownloadedBytes = usedBytes
    };
}
