namespace OpenSandbox.Web.Host.Web;

public static class SpaFallbackEndpoints
{
    public static WebApplication MapSpaFallback(this WebApplication app)
    {
        // dev（dotnet run）时前端产物随 csproj Content 复制到 bin 输出目录；
        // 生产发布时在 ContentRoot/wwwroot。仅当 index.html 存在时才启用托管，
        // 纯 API 模式（无前端构建）下不注册 fallback。
        if (app.Environment.IsDevelopment())
        {
            // WebRootPath 赋值不会刷新已缓存的 WebRootFileProvider（静态 Web 资源会提前包装它），
            // 因此显式替换 provider，让静态文件与 fallback 指向 bin 输出目录。
            var devWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            app.Environment.WebRootPath = devWebRoot;
            app.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(devWebRoot);
        }

        var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
        if (File.Exists(indexPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}
