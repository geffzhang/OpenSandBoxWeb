using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Infrastructure.DependencyInjection;

public static class GatewayRegistration
{
    public static IServiceCollection AddOpenSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenSandboxOptions>(configuration.GetSection(OpenSandboxOptions.SectionName));
        return services;
    }
}
