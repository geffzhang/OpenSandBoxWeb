using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;

namespace OpenSandbox.Web.Host.Web;

public static class SpaFallbackEndpoints
{
    public static WebApplication MapSpaFallback(this WebApplication app)
    {
        // dev（dotnet run）时前端产物随 csproj Content 复制到 bin 输出目录；
        // 生产发布时在 ContentRoot/wwwroot。仅当 index.html 存在时才启用托管，
        // 纯 API 模式（无前端构建）下不注册 fallback。
        var webRoot = app.Environment.IsDevelopment()
            ? Path.Combine(AppContext.BaseDirectory, "wwwroot")
            : app.Environment.WebRootPath ?? "wwwroot";

        var indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath))
            return app;

        if (app.Environment.IsDevelopment())
        {
            // 主机可能在此时已缓存 WebRootFileProvider，因此同时显式指向 bin 输出目录。
            app.Environment.WebRootPath = webRoot;
            app.Environment.WebRootFileProvider =
                new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
        }

        var auth = app.Services.GetRequiredService<IOptions<ConsoleAuthOptions>>().Value;

        // index.html 必须带运行时登录配置，且不能被浏览器缓存。
        app.Use(async (context, next) =>
        {
            if (!IsIndexRequest(context.Request))
            {
                await next();
                return;
            }

            await WriteIndexAsync(context, indexPath, auth);
        });

        // 静态资源在认证之前处理；API 永远不落到 SPA。
        app.UseStaticFiles();

        app.MapFallback(async (HttpContext context) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "not_found",
                    message = "接口不存在",
                });
                return;
            }

            await WriteIndexAsync(context, indexPath, auth);
        }).AllowAnonymous();

        return app;
    }

    private static bool IsIndexRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
        (request.Path == "/" ||
         request.Path.Value?.Equals("/index.html", StringComparison.OrdinalIgnoreCase) == true);

    private static async Task WriteIndexAsync(
        HttpContext context,
        string indexPath,
        ConsoleAuthOptions auth)
    {
        var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(
            AuthConfigInjection.Inject(html, auth), context.RequestAborted);
    }
}
