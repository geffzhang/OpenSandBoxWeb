using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.DependencyInjection;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeManagerRegistrationTests
{
    [Fact]
    public void NonPositiveScanInterval_FailsOptionsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:VolumeManager"] = "Host=unused;Database=unused",
            ["VolumeManager:Enabled"] = "false",
            ["VolumeManager:ScanInterval"] = "00:00:00",
        });

        var options = provider.GetRequiredService<IOptions<VolumeManagerOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains("VolumeManager:ScanInterval 必须大于零", exception.Message);
    }

    [Fact]
    public void NonPositiveMaxConcurrentNamespaces_FailsOptionsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:VolumeManager"] = "Host=unused;Database=unused",
            ["VolumeManager:Enabled"] = "false",
            ["VolumeManager:MaxConcurrentNamespaces"] = "0",
        });

        var options = provider.GetRequiredService<IOptions<VolumeManagerOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains("VolumeManager:MaxConcurrentNamespaces 必须至少为 1", exception.Message);
    }

    [Fact]
    public void ValidConfiguration_BindsVolumeManagerOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:VolumeManager"] = "Host=unused;Database=unused",
            ["VolumeManager:Enabled"] = "true",
            ["VolumeManager:ScanInterval"] = "00:10:00",
            ["VolumeManager:ScanOnStartup"] = "false",
            ["VolumeManager:MaxConcurrentNamespaces"] = "2",
            ["VolumeManager:MigrateOnStartup"] = "false",
        });

        var options = provider.GetRequiredService<IOptions<VolumeManagerOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ScanInterval);
        Assert.False(options.ScanOnStartup);
        Assert.Equal(2, options.MaxConcurrentNamespaces);
        Assert.False(options.MigrateOnStartup);
    }

    [Fact]
    public void ConcreteOptionsSingleton_ComesFromValidatedOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:VolumeManager"] = "Host=unused;Database=unused",
            ["VolumeManager:Enabled"] = "false",
        });

        var concrete = provider.GetRequiredService<VolumeManagerOptions>();
        var validated = provider.GetRequiredService<IOptions<VolumeManagerOptions>>().Value;

        Assert.Same(validated, concrete);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MigrateVolumeManager_EarlyReturn_DoesNotRequireVolumeDbContext(
        bool enabled, bool migrateOnStartup)
    {
        var services = new ServiceCollection();
        services.AddOptions<VolumeManagerOptions>().Configure(options =>
        {
            options.Enabled = enabled;
            options.MigrateOnStartup = migrateOnStartup;
        });
        await using var provider = services.BuildServiceProvider();

        await provider.MigrateVolumeManagerAsync();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVolumeManager(configuration);
        return services.BuildServiceProvider();
    }
}
