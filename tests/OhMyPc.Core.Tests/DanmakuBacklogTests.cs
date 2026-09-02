using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Core.Tests;

public sealed class DanmakuBacklogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_SameSourceAndTitle_MergesAndKeepsLatestBody()
    {
        var backlog = new DanmakuBacklog();

        backlog.Add(Record("quota", "模型告警", "A 服务不可用", NotificationSeverity.Warning), T0);
        backlog.Add(Record("quota", "模型告警", "A 服务已恢复", NotificationSeverity.Info), T0.AddMinutes(5));

        var drained = backlog.Drain();
        var entry = Assert.Single(drained.Entries);
        Assert.Equal(2, entry.Count);
        Assert.Equal("A 服务已恢复", entry.Latest.Body);
        Assert.Equal(NotificationSeverity.Info, entry.Latest.Severity);
        Assert.Equal(T0, entry.FirstAt);
        Assert.Equal(T0.AddMinutes(5), entry.LastAt);
        Assert.Equal(0, drained.DroppedCount);
    }

    [Fact]
    public void Add_IdenticalMessages_OnlyCountsUp()
    {
        var backlog = new DanmakuBacklog();
        var message = Record("api", "提醒", "同一条消息", NotificationSeverity.Info);

        backlog.Add(message, T0);
        backlog.Add(message, T0.AddMinutes(1));
        backlog.Add(message, T0.AddMinutes(2));

        var entry = Assert.Single(backlog.Drain().Entries);
        Assert.Equal(3, entry.Count);
    }

    [Fact]
    public void Add_DifferentSourceOrTitle_KeptSeparately()
    {
        var backlog = new DanmakuBacklog();

        backlog.Add(Record("a", "标题", "内容", NotificationSeverity.Info), T0);
        backlog.Add(Record("b", "标题", "内容", NotificationSeverity.Info), T0);
        backlog.Add(Record("a", "另一个标题", "内容", NotificationSeverity.Info), T0);

        Assert.Equal(3, backlog.Drain().Entries.Count);
    }

    [Fact]
    public void Add_OverflowDropsOldestAndCounts()
    {
        var backlog = new DanmakuBacklog();
        for (var i = 0; i < DanmakuBacklog.MaxEntries + 3; i++)
        {
            backlog.Add(Record("src", $"标题 {i}", "内容", NotificationSeverity.Info), T0.AddMinutes(i));
        }

        var drained = backlog.Drain();
        Assert.Equal(3, drained.DroppedCount);
        Assert.Equal(DanmakuBacklog.MaxEntries, drained.Entries.Count);
        // 最旧的 3 条被丢弃，剩下的从“标题 3”开始
        Assert.Equal("标题 3", drained.Entries[0].Latest.Title);
        Assert.Equal($"标题 {DanmakuBacklog.MaxEntries + 2}", drained.Entries[^1].Latest.Title);
    }

    [Fact]
    public void Drain_ClearsStateAndRestorePutsEntryBack()
    {
        var backlog = new DanmakuBacklog();
        backlog.Add(Record("a", "标题", "内容", NotificationSeverity.Info), T0);
        var first = backlog.Drain();
        Assert.Empty(backlog.Drain().Entries);

        backlog.Add(Record("b", "其他", "内容", NotificationSeverity.Info), T0);
        foreach (var entry in first.Entries) backlog.Restore(entry);

        var second = backlog.Drain();
        Assert.Equal(2, second.Entries.Count);
        // Restore 原样放回，不重置次数
        Assert.Equal(1, Assert.Single(second.Entries, e => e.Latest.Source == "a").Count);
    }

    private static NotificationRecord Record(
        string source,
        string title,
        string body,
        NotificationSeverity severity) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Origin = NotificationOrigin.Automation,
        Source = source,
        Title = title,
        Body = body,
        Channels = NotificationChannels.Danmaku,
        Severity = severity,
        CreatedAt = T0
    };
}
