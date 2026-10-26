using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSandbox.Web.Infrastructure.Gateways;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using OpenSandbox.Web.Tests.Volumes;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeSandboxGateway Gateway { get; } = new();
    public FakeVolumeKubernetesClient VolumeKube { get; } =
        FakeVolumeKubernetesClient.WithManagedNamespace();

    public ApiFactory()
    {
        _connection.Open();
        using var db = NewVolumeDb();
        db.Database.EnsureCreated();
    }

    public VolumeDbContext NewVolumeDb()
    {
        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new VolumeDbContext(options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:VolumeManager", "Host=unused;Database=unused");
        builder.UseSetting("VolumeManager:Enabled", "false");
        builder.UseSetting("VolumeManager:MigrateOnStartup", "false");
        builder.UseSetting("VolumeManager:ScanOnStartup", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);

            services.RemoveAll<DbContextOptions<VolumeDbContext>>();
            services.RemoveAll<VolumeDbContext>();
            // EF Core 9+ 把 optionsAction 注册为 IDbContextOptionsConfiguration，只删 options 会残留 Npgsql 配置。
            services.RemoveAll<IDbContextOptionsConfiguration<VolumeDbContext>>();
            services.AddDbContext<VolumeDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IVolumeKubernetesClient>();
            services.AddSingleton<IVolumeKubernetesClient>(VolumeKube);

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }

    public new HttpClient CreateClient() =>
        CreateClientWithRoles("sandbox:admin", "volume:admin");

    public HttpClient CreateClientWithRoles(params string[] roles)
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "test-admin");
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    public HttpClient CreateAnonymousClient() => base.CreateClient();

    public HttpClient CreateTenantClient(string apiKey)
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
