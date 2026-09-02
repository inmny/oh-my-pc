using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

/// <summary>
/// 离开期间积压的弹幕：按（来源, 标题）合并——正文与级别取最新、次数累计（状态翻转的消息只保留最终状态）；
/// 超过上限丢弃最旧条目并计数，避免回来后被大量重复/冲突消息刷屏。
/// </summary>
public sealed class DanmakuBacklog
{
    public const int MaxEntries = 10;

    private readonly List<DanmakuBacklogEntry> _entries = [];
    private int _droppedCount;

    public void Add(NotificationRecord record, DateTimeOffset at)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (!string.Equals(entry.Latest.Source, record.Source, StringComparison.Ordinal)
                || !string.Equals(entry.Latest.Title, record.Title, StringComparison.Ordinal)) continue;
            entry.Latest = record;
            entry.Count++;
            entry.LastAt = at;
            return;
        }

        if (_entries.Count >= MaxEntries) DropOldest();
        _entries.Add(new DanmakuBacklogEntry { Latest = record, Count = 1, FirstAt = at, LastAt = at });
    }

    /// <summary>补播中途用户再次离开时，把尚未播放的条目原样放回。</summary>
    public void Restore(DanmakuBacklogEntry entry)
    {
        if (_entries.Count >= MaxEntries) DropOldest();
        _entries.Add(entry);
    }

    public DanmakuBacklogDrainResult Drain()
    {
        var entries = _entries.ToArray();
        _entries.Clear();
        var dropped = _droppedCount;
        _droppedCount = 0;
        return new DanmakuBacklogDrainResult(entries, dropped);
    }

    private void DropOldest()
    {
        _entries.RemoveAt(0);
        _droppedCount++;
    }
}

public sealed record DanmakuBacklogEntry
{
    public required NotificationRecord Latest { get; set; }
    public int Count { get; set; }
    public DateTimeOffset FirstAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
}

public sealed record DanmakuBacklogDrainResult(IReadOnlyList<DanmakuBacklogEntry> Entries, int DroppedCount);
