namespace OpenSandbox.Web.Host.Web;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", () => Results.Ok(new
        {
            status = "ok",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        })).AllowAnonymous();
        return app;
    }
}
