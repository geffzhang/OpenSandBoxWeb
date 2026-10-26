using System.Security.Claims;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeAgentEndpoints
{
    public static IEndpointRouteBuilder MapVolumeAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/volumes");

        group.MapPost("/register", async (
            VolumeRegistrationRequest request,
            HttpContext context,
            RegistrationService service,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirstValue("tenant");
            if (tenantId is null)
                return Results.Unauthorized();
            return Results.Ok(await service.RegisterAsync(
                tenantId, request, $"tenant:{tenantId}", ct));
        });

        group.MapPost("/{namespaceName}/{claimName}/lifecycle", async (
            string namespaceName,
            string claimName,
            VolumeLifecycleRequest request,
            HttpContext context,
            LifecycleService service,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirstValue("tenant");
            if (tenantId is null)
                return Results.Unauthorized();
            return Results.Ok(await service.ChangeAsync(
                tenantId, namespaceName, claimName, request, false,
                $"tenant:{tenantId}", ct));
        });

        return app;
    }
}
