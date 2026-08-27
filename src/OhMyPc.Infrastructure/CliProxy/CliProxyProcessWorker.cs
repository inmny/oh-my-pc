using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>按设置自动拉起 CLIProxyAPI，并周期性刷新运行状态供界面展示。</summary>
public sealed class CliProxyProcessWorker(
    IAppStore store,
    ICliProxyInstaller installer,
    ICliProxyProcessService process,
    IProxyStatusService status,
    ILogger<CliProxyProcessWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await store.GetSettingsAsync(stoppingToken);
            if (settings.CliProxyAutoStart && installer.IsInstalled())
            {
                await process.StartAsync(stoppingToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "CLIProxyAPI 自动启动失败");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await status.RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "CLIProxyAPI 状态刷新失败");
            }
        }
    }
}
