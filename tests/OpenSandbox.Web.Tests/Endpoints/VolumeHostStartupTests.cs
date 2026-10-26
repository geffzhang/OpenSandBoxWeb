using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Web;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeHostStartupTests
{
    [Fact]
    public void InvalidScanInterval_AbortsHostStartup()
    {
        using var factory = new InvalidScanIntervalFactory();

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var validation = FindOptionsValidationException(exception!);
        Assert.NotNull(validation);
        Assert.Contains("VolumeManager:ScanInterval 必须大于零", validation!.Message);
    }

    [Fact]
    public async Task Readyz_WithUnreachableDatabase_Returns503()
    {
        // SQLite 在父目录不存在时打开失败，用它在不依赖 Postgres 的情况下模拟依赖不可用。
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"osb-missing-{Guid.NewGuid():N}", "volumes.db");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOptions<VolumeManagerOptions>()
            .Configure(options => options.Enabled = true);
        builder.Services.AddDbContext<VolumeDbContext>(
            options => options.UseSqlite($"Data Source={dbPath}"));
        await using var app = builder.Build();
        app.MapOpsEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReadyBody>();
        Assert.Equal("not-ready", body!.Status);
        await app.StopAsync();
    }

    private static OptionsValidationException? FindOptionsValidationException(
        Exception exception)
    {
        if (exception is OptionsValidationException validation)
            return validation;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                var found = FindOptionsValidationException(inner);
                if (found is not null)
                    return found;
            }
        }

        return exception.InnerException is null
            ? null
            : FindOptionsValidationException(exception.InnerException);
    }

    private sealed record ReadyBody(string Status);

    private sealed class InvalidScanIntervalFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:VolumeManager", "Host=unused;Database=unused");
            builder.UseSetting("VolumeManager:Enabled", "false");
            builder.UseSetting("VolumeManager:ScanInterval", "00:00:00");
        }
    }
}
