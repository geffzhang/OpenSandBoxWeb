using System.Text.Json;
using OpenSandbox.Web.Host.Web;
using OpenSandbox.Web.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddOpenSandbox(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<SandboxExceptionMiddleware>();

app.MapHealthEndpoints();
app.MapSandboxEndpoints();
app.MapMetricsEndpoints();

app.Run();

public partial class Program;
