using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OhMyPc.Core;

namespace OhMyPc.Infrastructure.Presence;

/// <summary>
/// 判定用户是否在电脑前：键鼠空闲（GetLastInputInfo）超过 5 分钟或会话锁定视为离开；
/// 出现新的输入（10 秒内轮询到）或解锁即恢复在场。
/// </summary>
public sealed class UserPresenceService(
    ILogger<UserPresenceService> logger) : IUserPresenceService, IHostedService, IDisposable
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private bool _started;
    private volatile bool _sessionLocked;
    private volatile bool _idleAway;

    public bool IsAway => _sessionLocked || _idleAway;

    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        SystemEvents.SessionSwitch += SessionSwitch;
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return Task.CompletedTask;
        _started = false;
        SystemEvents.SessionSwitch -= SessionSwitch;
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UpdateIdleState();
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateIdleState()
    {
        var wasAway = IsAway;
        _idleAway = GetIdleMilliseconds() >= IdleThreshold.TotalMilliseconds;
        RaiseIfChanged(wasAway);
    }

    private void SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var wasAway = IsAway;
        if (e.Reason == SessionSwitchReason.SessionLock) _sessionLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _sessionLocked = false;
        RaiseIfChanged(wasAway);
    }

    private void RaiseIfChanged(bool wasAway)
    {
        if (wasAway == IsAway) return;
        logger.LogDebug("用户在场状态变化：{State}", IsAway ? "离开" : "回来");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static long GetIdleMilliseconds()
    {
        var info = default(LastInputInfo);
        info.Size = (uint)Marshal.SizeOf<LastInputInfo>();
        return GetLastInputInfo(ref info)
            ? unchecked((uint)Environment.TickCount - info.Timestamp)
            : 0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Timestamp;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
