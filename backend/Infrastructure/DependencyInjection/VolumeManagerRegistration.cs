using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.DependencyInjection;

public static class VolumeManagerRegistration
{
    public static IServiceCollection AddVolumeManager(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(VolumeManagerOptions.SectionName);
        var bound = section.Get<VolumeManagerOptions>() ?? new VolumeManagerOptions();
        services.AddOptions<VolumeManagerOptions>()
            .Bind(section)
            .Validate(options => options.ScanInterval > TimeSpan.Zero, "VolumeManager:ScanInterval 必须大于零")
            .Validate(options => options.MaxConcurrentNamespaces >= 1, "VolumeManager:MaxConcurrentNamespaces 必须至少为 1")
            .ValidateOnStart();
        services.AddSingleton(bound);

        var connectionString = configuration.GetConnectionString("VolumeManager");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (bound.Enabled)
                throw new InvalidOperationException("ConnectionStrings:VolumeManager 未配置");
            connectionString =
                "Host=localhost;Database=opensandbox;Username=opensandbox;Password=opensandbox";
        }

        services.AddDbContext<VolumeDbContext>(builder => builder.UseNpgsql(connectionString));
        services.AddSingleton<IKubernetes>(_ => BuildKubernetes(bound));
        services.AddSingleton<IVolumeKubernetesClient, KubernetesVolumeClient>();
        services.AddSingleton<VolumeLabelWriter>();
        services.AddSingleton<ScanCoordinator>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<InventoryScanService>();
        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<TenantService>();
        services.AddScoped<VolumeQueries>();
        services.AddScoped<RegistrationService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<ImportService>();
        services.AddHostedService<InventoryScanner>();
        return services;
    }

    public static async Task MigrateVolumeManagerAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<VolumeManagerOptions>>().Value;
        if (!options.Enabled || !options.MigrateOnStartup)
            return;

        var db = scope.ServiceProvider.GetRequiredService<VolumeDbContext>();
        await db.Database.MigrateAsync(ct);
    }

    private static IKubernetes BuildKubernetes(VolumeManagerOptions options)
    {
        var configuration = string.IsNullOrWhiteSpace(options.KubeConfigPath)
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(options.KubeConfigPath);
        return new Kubernetes(configuration);
    }
}
