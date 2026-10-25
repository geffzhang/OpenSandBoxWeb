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
            app.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
        }

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");
        return app;
    }
}
