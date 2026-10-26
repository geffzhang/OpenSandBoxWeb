using Microsoft.Extensions.Options;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class InventoryScanner(
    ScanCoordinator coordinator,
    IOptions<VolumeManagerOptions> options,
    ILogger<InventoryScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        if (options.Value.ScanOnStartup)
            await RunAsync("startup", stoppingToken);

        using var timer = new PeriodicTimer(options.Value.ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunAsync("scheduled", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(string trigger, CancellationToken ct)
    {
        try
        {
            await coordinator.ScanAsync(trigger, ct);
        }
        catch (ScanInProgressException)
        {
            logger.LogInformation("上一轮扫描仍在运行，跳过 {Trigger} 扫描", trigger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "{Trigger} 扫描失败", trigger);
        }
    }
}
