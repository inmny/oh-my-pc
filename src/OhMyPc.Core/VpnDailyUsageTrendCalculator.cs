using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

public sealed class VpnDailyUsageTrend
{
    public IReadOnlyDictionary<DateOnly, double> DailyBytes { get; init; }
        = new Dictionary<DateOnly, double>();
    public double AverageDailyBytes { get; init; }
}

public static class VpnDailyUsageTrendCalculator
{
    public static VpnDailyUsageTrend Calculate(
        IReadOnlyList<VpnDailyUsagePoint> history,
        DateOnly from,
        DateOnly to)
    {
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to));

        var dailyBytes = new Dictionary<DateOnly, double>();
        var totalBytes = 0d;
        var totalDays = 0;
        var ordered = history.OrderBy(point => point.Date).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var intervalDays = current.Date.DayNumber - previous.Date.DayNumber;
            if (intervalDays <= 0) continue;

            var intervalBytes = current.UsedBytes - previous.UsedBytes;
            if (intervalBytes < 0)
            {
                if (intervalDays > 1) continue;
                intervalBytes = current.UsedBytes;
            }

            var averageBytes = intervalBytes / (double)intervalDays;
            var intervalFrom = previous.Date.AddDays(1);
            var visibleFrom = intervalFrom < from ? from : intervalFrom;
            var visibleTo = current.Date > to ? to : current.Date;
            if (visibleTo < visibleFrom) continue;

            for (var date = visibleFrom; date <= visibleTo; date = date.AddDays(1))
            {
                dailyBytes[date] = averageBytes;
            }

            var visibleDays = visibleTo.DayNumber - visibleFrom.DayNumber + 1;
            totalBytes += averageBytes * visibleDays;
            totalDays += visibleDays;
        }

        return new VpnDailyUsageTrend
        {
            DailyBytes = dailyBytes,
            AverageDailyBytes = totalDays == 0 ? 0 : totalBytes / totalDays
        };
    }
}
