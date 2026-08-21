using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.Providers;

public sealed class QuotaPollingWorker(
    EnvironmentSourceImporter importer,
    QuotaRefreshService refreshService,
    ILogger<QuotaPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await importer.ImportAsync(stoppingToken);
            await refreshService.RefreshAllAsync(cancellationToken: stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Initial quota refresh failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await refreshService.RefreshAllAsync(onlyDue: true, cancellationToken: stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Scheduled quota refresh failed");
            }
        }
    }
}
