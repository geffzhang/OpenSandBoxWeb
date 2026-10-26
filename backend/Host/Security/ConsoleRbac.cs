using Microsoft.Extensions.Options;

namespace OpenSandbox.Web.Host.Security;

public enum RbacAction
{
    Skip,
    AnyAuthenticated,
    RequireRole,
    Deny,
}

public readonly record struct RbacDecision(RbacAction Action, string? Role = null);

public static class ConsoleRbac
{
    public static RbacDecision Decide(PathString path, string method)
    {
        if (!path.StartsWithSegments("/api"))
            return new RbacDecision(RbacAction.Skip);

        if (HttpMethods.IsOptions(method))
            return new RbacDecision(RbacAction.Skip);

        if (path.StartsWithSegments("/api/v1/health"))
            return new RbacDecision(RbacAction.Skip);

        if (path.StartsWithSegments("/api/v1/tenant"))
            return new RbacDecision(RbacAction.Skip);

        if (path.StartsWithSegments("/api/v1/console"))
            return new RbacDecision(RbacAction.AnyAuthenticated);

        if (path.StartsWithSegments("/api/v1/sandboxes"))
        {
            if (!IsRead(method))
                return new RbacDecision(RbacAction.RequireRole, ConsoleRoles.SandboxAdmin);

            // 数据面入口 URL 与写操作同权，先于通用沙箱 GET 规则。
            if (path.Value?.Contains("/endpoints/", StringComparison.OrdinalIgnoreCase) == true)
                return new RbacDecision(RbacAction.RequireRole, ConsoleRoles.SandboxAdmin);

            return new RbacDecision(RbacAction.RequireRole, ConsoleRoles.SandboxViewer);
        }

        if (path.StartsWithSegments("/api/v1/volumes") ||
            path.StartsWithSegments("/api/v1/volume-tenants"))
        {
            return new RbacDecision(
                RbacAction.RequireRole,
                IsRead(method) ? ConsoleRoles.VolumeViewer : ConsoleRoles.VolumeAdmin);
        }

        return new RbacDecision(RbacAction.Deny);
    }

    private static bool IsRead(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
}

public sealed class ConsoleRbacMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConsoleAuthOptions _options;

    public ConsoleRbacMiddleware(RequestDelegate next, IOptions<ConsoleAuthOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var decision = ConsoleRbac.Decide(context.Request.Path, context.Request.Method);
        if (decision.Action == RbacAction.Skip)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized,
                new { code = "unauthorized", message = "需要登录" });
            return;
        }

        if (decision.Action == RbacAction.AnyAuthenticated)
        {
            await _next(context);
            return;
        }

        var roles = ConsoleRoles.RolesOf(context.User, _options.ClientId);

        if (decision.Action == RbacAction.Deny)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, new
            {
                code = "forbidden",
                message = "未登记的 API 路径",
                roles = roles.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
            });
            return;
        }

        if (!ConsoleRoles.Has(roles, decision.Role!))
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, new
            {
                code = "forbidden",
                message = $"需要角色 {decision.Role}",
                roles = roles.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
            });
            return;
        }

        await _next(context);
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, object body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        if (statusCode == StatusCodes.Status401Unauthorized)
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
        await context.Response.WriteAsJsonAsync(body);
    }
}

public static class ConsoleRbacExtensions
{
    public static IApplicationBuilder UseConsoleRbac(this IApplicationBuilder app) =>
        app.UseMiddleware<ConsoleRbacMiddleware>();
}
