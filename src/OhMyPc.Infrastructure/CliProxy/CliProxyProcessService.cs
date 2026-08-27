using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>cli-proxy-api.exe 的进程生命周期管理；oh-my-pc 退出不杀进程，保持下游编码工具可用。</summary>
public sealed class CliProxyProcessService(
    ICliProxyInstaller installer,
    IProxyConfigStore store,
    IProxyStatusService status,
    ILogger<CliProxyProcessService> logger) : ICliProxyProcessService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public event EventHandler? StateChanged;

    public ProxyProcessState State { get; private set; } = ProxyProcessState.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!installer.IsInstalled()) throw new InvalidOperationException("CLIProxyAPI 尚未安装。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsPortReadyAsync(cancellationToken))
            {
                SetState(ProxyProcessState.Running);
                return;
            }
            // 全新安装可能还没有 config.yaml，启动前先补一份最小配置。
            await store.EnsureConfigAsync(cancellationToken);
            SetState(ProxyProcessState.Starting);
            var startInfo = new ProcessStartInfo
            {
                FileName = CliProxyPaths.ExecutablePath,
                Arguments = $"-config \"{CliProxyPaths.ConfigPath}\"",
                WorkingDirectory = CliProxyPaths.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 cli-proxy-api 进程。");

            var deadline = Environment.TickCount64 + (long)StartupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(500, cancellationToken);
                if (await IsPortReadyAsync(cancellationToken))
                {
                    SetState(ProxyProcessState.Running);
                    return;
                }
                if (_process.HasExited) break;
            }

            var exitSuffix = _process.HasExited ? $"（退出码 {_process.ExitCode}，请检查端口占用或配置）" : "（端口一直未就绪，请检查端口占用）";
            _process = null;
            SetState(ProxyProcessState.Stopped);
            throw new InvalidOperationException($"CLIProxyAPI 启动失败{exitSuffix}。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var process in FindOurProcesses())
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    logger.LogWarning(exception, "停止 cli-proxy-api 进程失败（PID {Pid}）", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
            _process = null;
            SetState(ProxyProcessState.Stopped);
            await status.RefreshAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    /// <summary>按可执行文件路径过滤本安装目录下的 cli-proxy-api 进程（避免误杀 EasyCPA 的旧实例）。</summary>
    private static Process[] FindOurProcesses() =>
        Process.GetProcessesByName("cli-proxy-api")
            .Where(process =>
            {
                try
                {
                    return string.Equals(process.MainModule?.FileName, CliProxyPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    return false;
                }
            })
            .ToArray();

    private async Task<bool> IsPortReadyAsync(CancellationToken cancellationToken) =>
        (await status.RefreshAsync(cancellationToken)).State == ProxyProcessState.Running;

    private void SetState(ProxyProcessState value)
    {
        if (State == value) return;
        State = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
