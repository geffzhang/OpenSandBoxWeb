using Microsoft.Extensions.DependencyInjection;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class ScanInProgressException() : Exception("上一轮扫描仍在运行");

public sealed class ScanCoordinator(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<ScanSummary> ScanAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new ScanInProgressException();

        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<InventoryScanService>();
            return await service.ScanAsync(trigger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
