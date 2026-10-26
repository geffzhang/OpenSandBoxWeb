using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class InventoryScannerTests
{
    [Fact]
    public async Task Disabled_WithScanOnStartup_DoesNotScanAndLogsNoError()
    {
        var scopes = new CountingScopeFactory();
        var logger = new CapturingLogger();
        var scanner = CreateScanner(
            scopes,
            logger,
            new VolumeManagerOptions
            {
                Enabled = false,
                ScanOnStartup = true,
                ScanInterval = TimeSpan.FromHours(1),
            });

        await scanner.StartAsync(CancellationToken.None);
        await scanner.StopAsync(CancellationToken.None);

        Assert.Equal(0, scopes.Attempts);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Enabled_WithScanOnStartup_AttemptsStartupScanOnceAndLogsFailure()
    {
        var scopes = new CountingScopeFactory();
        var logger = new CapturingLogger();
        var scanner = CreateScanner(
            scopes,
            logger,
            new VolumeManagerOptions
            {
                Enabled = true,
                ScanOnStartup = true,
                ScanInterval = TimeSpan.FromHours(1),
            });

        await scanner.StartAsync(CancellationToken.None);
        // .NET 10 的 BackgroundService 在线程池上启动 ExecuteAsync，等待日志信号避免竞态。
        await logger.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(5));
        await scanner.StopAsync(CancellationToken.None);

        Assert.Equal(1, scopes.Attempts);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("startup 扫描失败", error.Message);
        var exception = Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal("不应扫描", exception.Message);
    }

    private static InventoryScanner CreateScanner(
        IServiceScopeFactory scopes,
        ILogger<InventoryScanner> logger,
        VolumeManagerOptions options) =>
        new(new ScanCoordinator(scopes), Options.Create(options), logger);

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        public int Attempts { get; private set; }

        public IServiceScope CreateScope()
        {
            Attempts++;
            throw new InvalidOperationException("不应扫描");
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<InventoryScanner>
    {
        private readonly TaskCompletionSource _errorLogged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LogEntry> Entries { get; } = [];

        public Task ErrorLogged => _errorLogged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            if (logLevel == LogLevel.Error)
                _errorLogged.TrySetResult();
        }
    }
}
