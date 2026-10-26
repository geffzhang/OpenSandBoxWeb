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
builder.Services.AddVolumeManager(builder.Configuration);

var app = builder.Build();

await app.Services.MigrateVolumeManagerAsync();

app.UseMiddleware<SandboxExceptionMiddleware>();

// 静态资源与 SPA fallback 必须匿名；index.html 在这里注入登录配置。
app.MapSpaFallback();

// 卷异常中间件必须在 SandboxExceptionMiddleware 之内，否则会被它先翻译成 500。
app.UseMiddleware<VolumeExceptionMiddleware>();
// 租户 Key 必须在 UseAuthentication 之前写入 Principal；TestAuthHandler 无头时返回 NoResult，不会覆盖。
app.UseMiddleware<TenantApiKeyMiddleware>();

app.UseAuthentication();
app.UseConsoleRbac();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapOpsEndpoints();
app.MapSandboxEndpoints();
app.MapMetricsEndpoints();
app.MapConsoleEndpoints();
app.MapVolumeTenantEndpoints();
app.MapVolumeAgentEndpoints();
app.MapVolumeAdminEndpoints();

app.Run();

public partial class Program;
