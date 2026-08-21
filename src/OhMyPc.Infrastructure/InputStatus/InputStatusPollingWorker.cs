using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.InputStatus;

public sealed class InputStatusPollingWorker(
    InputStatusRefreshService refreshService,
    ILogger<InputStatusPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await refreshService.RefreshAllAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Input status refresh failed");
        }
    }
}
