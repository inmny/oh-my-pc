using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>
/// 本机 dsh web 实例的生命周期管理。dsh 在 Windows 上是 npm 批处理壳（真进程为 node 子进程），
/// 因此用 cmd 拉起、按进程树终止；用户在终端自行启动的实例识别为“外部实例”，只读展示。
/// </summary>
public sealed class LocalDshProcessService(ILogger<LocalDshProcessService> logger) : ILocalDshManager
{
    private static readonly Regex PanelUrlRegex = new(@"dsh web: (https?://\S+)", RegexOptions.Compiled);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient HttpProbeClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentOutput = new();
    private Process? _process;
    private int _port;
    private string? _panelUrl;
    private DshInstanceSnapshot _snapshot = new();

    public event EventHandler? StateChanged;

    public DshInstanceSnapshot Snapshot => _snapshot;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await TryGetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (_process is { HasExited: false })
            {
                SetSnapshot(new DshInstanceSnapshot
                {
                    State = _panelUrl is null ? DshRunState.Starting : DshRunState.Running,
                    Port = _port,
                    Version = version,
                    PanelUrl = _panelUrl
                });
                return;
            }

            var probePort = _port == 0 ? DefaultPort : _port;
            if (await ProbeHttpAliveAsync(probePort, cancellationToken).ConfigureAwait(false))
            {
                SetSnapshot(new DshInstanceSnapshot { State = DshRunState.External, Port = probePort, Version = version });
            }
            else
            {
                _process = null;
                _panelUrl = null;
                SetSnapshot(new DshInstanceSnapshot { State = DshRunState.Stopped, Version = version });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("本机 dsh web 已在运行。");
            }

            if (await ProbeHttpAliveAsync(port, cancellationToken).ConfigureAwait(false))
            {
                SetSnapshot(new DshInstanceSnapshot
                {
                    State = DshRunState.External,
                    Port = port,
                    Version = _snapshot.Version,
                    Error = $"端口 {port} 已有 web 在监听（可能是外部实例），请先停止它或更换端口。"
                });
                throw new InvalidOperationException($"端口 {port} 已有 web 在监听（可能是外部实例），请先停止它或更换端口。");
            }

            var dshPath = FindDshCommand()
                ?? throw new InvalidOperationException("未找到 dsh 命令：请先执行 npm install -g @deepseek-ai/dsh 安装。");

            _recentOutput.Clear();
            _port = port;
            _panelUrl = null;
            var startInfo = BuildStartInfo(dshPath, port);
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 dsh 进程。");
            _ = PumpAsync(_process.StandardOutput);
            _ = PumpAsync(_process.StandardError);
            SetSnapshot(new DshInstanceSnapshot { State = DshRunState.Starting, Port = port, Version = _snapshot.Version });

            var deadline = DateTimeOffset.UtcNow + StartupTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_panelUrl is not null)
                {
                    SetSnapshot(new DshInstanceSnapshot
                    {
                        State = DshRunState.Running,
                        Port = port,
                        Version = _snapshot.Version,
                        PanelUrl = _panelUrl
                    });
                    return;
                }

                if (_process.HasExited)
                {
                    var tail = string.Join(Environment.NewLine, _recentOutput.TakeLast(8));
                    throw new InvalidOperationException($"dsh 启动失败（退出码 {_process.ExitCode}）：\n{tail}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("dsh 启动超时：20 秒内未打印面板地址，请检查端口占用。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not { HasExited: false } process)
            {
                _process = null;
                SetSnapshot(_snapshot with { State = DshRunState.Stopped, PanelUrl = null });
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (killer is not null) await killer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or SystemException)
            {
                logger.LogWarning(exception, "停止 dsh 进程（PID {Pid}）失败", process.Id);
            }
            finally
            {
                _process?.Dispose();
                _process = null;
                _panelUrl = null;
            }

            SetSnapshot(_snapshot with { State = DshRunState.Stopped, PanelUrl = null });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>沿 PATH 查找 dsh 命令（Windows 上带 PATHEXT 后缀尝试）。</summary>
    internal static string? FindDshCommand() =>
        FindExecutable("dsh", Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT"), OperatingSystem.IsWindows());

    internal static string? FindExecutable(string name, string pathEnvironment, string? pathExtEnvironment, bool windows)
    {
        foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!windows)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
                continue;
            }

            var extensions = (string.IsNullOrEmpty(pathExtEnvironment) ? ".COM;.EXE;.BAT;.CMD" : pathExtEnvironment)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static ProcessStartInfo BuildStartInfo(string dshPath, int port)
    {
        var arguments = $"web --port {port} --no-open";
        if (OperatingSystem.IsWindows())
        {
            // cmd /c 走批处理壳；整体再加一层引号防路径空格
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{dshPath}\" {arguments}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = dshPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 40 && _recentOutput.TryDequeue(out _)) { }

            var match = PanelUrlRegex.Match(line);
            if (match.Success) _panelUrl = match.Groups[1].Value;
        }
    }

    private async Task<string?> TryGetVersionAsync(CancellationToken cancellationToken)
    {
        var dshPath = FindDshCommand();
        if (dshPath is null) return null;
        try
        {
            return await Task.Run(() =>
            {
                using var process = Process.Start(BuildVersionStartInfo(dshPath));
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000)) return null;
                return output.Trim().Split('\n', 2)[0].Trim() is { Length: > 0 } version ? version : null;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
            logger.LogWarning(exception, "读取本机 dsh 版本失败");
            return null;
        }
    }

    private static ProcessStartInfo BuildVersionStartInfo(string dshPath) =>
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{dshPath}\" --version\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
            : new ProcessStartInfo
            {
                FileName = dshPath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

    /// <summary>任何 HTTP 响应（含 401）都说明 web 在监听；连接拒绝或超时即未运行。</summary>
    private static async Task<bool> ProbeHttpAliveAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            using var response = await HttpProbeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private void SetSnapshot(DshInstanceSnapshot snapshot)
    {
        _snapshot = snapshot;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public const int DefaultPort = 3080;
}
