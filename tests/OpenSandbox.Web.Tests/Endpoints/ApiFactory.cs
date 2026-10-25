using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeSandboxGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);
        });
    }
}
