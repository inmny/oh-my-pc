using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.Vpn;

public sealed class VpnQuotaPollingWorker(
    VpnQuotaRefreshService refreshService,
    ILogger<VpnQuotaPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await refreshService.RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "VPN quota refresh failed");
        }
    }
}
