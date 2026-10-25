using System.Text.Json;
using OpenSandbox.Web.Host.Security;
using OpenSandbox.Web.Host.Web;
using OpenSandbox.Web.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddOpenSandbox(builder.Configuration);
builder.Services.AddConsoleAuth(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<SandboxExceptionMiddleware>();

// 静态资源与 SPA fallback 必须匿名；index.html 在这里注入登录配置。
app.MapSpaFallback();

app.UseAuthentication();
app.UseConsoleRbac();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapSandboxEndpoints();
app.MapMetricsEndpoints();
app.MapConsoleEndpoints();

app.Run();

public partial class Program;
