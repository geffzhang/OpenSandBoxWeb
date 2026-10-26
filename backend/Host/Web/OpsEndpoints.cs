using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Host.Web;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous();

        app.MapGet("/readyz", async (
            VolumeDbContext db,
            IOptions<VolumeManagerOptions> options,
            CancellationToken ct) =>
        {
            if (!options.Value.Enabled)
                return Results.Ok(new { status = "ready", checks = new { database = "disabled" } });

            try
            {
                if (await db.Database.CanConnectAsync(ct))
                    return Results.Ok(new { status = "ready", checks = new { database = "ok" } });
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }

            return Results.Json(
                new { status = "not-ready", checks = new { database = "fail" } },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        return app;
    }
}
