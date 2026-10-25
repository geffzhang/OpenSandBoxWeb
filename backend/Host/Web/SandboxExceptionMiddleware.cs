using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Host.Web;

public sealed class SandboxExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public SandboxExceptionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            var (statusCode, body) = SandboxExceptionTranslator.Translate(exception);
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(body);
        }
    }
}
