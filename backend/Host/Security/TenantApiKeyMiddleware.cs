using System.Security.Claims;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Security;

public sealed class TenantApiKeyMiddleware(
    RequestDelegate next,
    ILogger<TenantApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyAuthenticator authenticator)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/tenant"))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var rawKey = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

        TenantIdentity? identity;
        try
        {
            identity = await authenticator.AuthenticateAsync(rawKey, context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "租户 API Key 校验失败");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("dependency_unavailable", "认证服务暂不可用"));
            return;
        }

        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("unauthorized", "缺少或无效的租户 API Key"));
            return;
        }

        var claims = new[]
        {
            new Claim("tenant", identity.TenantId),
            new Claim("tenant_name", identity.DisplayName),
            new Claim("sub", $"tenant:{identity.TenantId}"),
            new Claim("auth_method", "tenant-key"),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TenantApiKey"));
        await next(context);
    }
}
