# OpenSandBoxWeb 前后端结构改造 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 OpenSandBoxWeb 从纯 Vue 前端重构为 goodcrew-pro 式前后端结构：React 19 + Vite 前端 + .NET 10 后端（Alibaba.OpenSandbox 1.1.1 SDK 重写管理 API）。

**Architecture:** 单仓库三目录（frontend/ backend/ tests/）。后端 ASP.NET Core Minimal API 按 Host/Web 端点 + Infrastructure/Gateways 分层，Gateway 封装 SDK（LifecycleStack/ExecdStack），异常经统一中间件翻译为 HTTP 错误；生产模式后端托管前端静态产物 + SPA fallback。前端 React Query 管服务端状态，指标走后端 SSE 代理（消除 CORS）。

**Tech Stack:** .NET 10 (SDK 10.0.401)、Alibaba.OpenSandbox 1.1.1、xUnit + WebApplicationFactory；React 19、Vite、Ant Design 5、@tanstack/react-query、echarts、vitest + Testing Library、oxlint。

**Spec:** docs/superpowers/specs/2026-10-25-opensandbox-web-restructure-design.md

## Global Constraints

- .NET SDK ≥ 10.0.401（已装）；Node ≥ 20（本机 v24.11.1）
- 后端端口 `http://localhost:5080`；前端 dev 端口 `3000`，proxy `/api` → 5080；API 前缀 `/api/v1`
- 唯一 NuGet 依赖：`Alibaba.OpenSandbox` 1.1.1（net10.0 目标）
- JSON 统一 camelCase；错误响应体恒为 `{code, message}`
- UI 文案中文；代码标识符英文
- 当前分支 `restructure-dotnet10-react`（不在 main 直接提交）
- 提交信息：`feat:/fix:/test:/docs:` 前缀 + 简短描述；每条提交末尾附 `Co-Authored-By: Claude Code <noreply@anthropic.com>`
- 验证命令：后端 `dotnet test`（在仓库根执行 `dotnet test tests/OpenSandbox.Web.Tests`）；前端 `cd frontend && npm test && npm run lint && npm run build`

---

### Task 1: 仓库骨架重组

**Files:**
- Create: `backend/`（空目录，Task 2 填充）
- Create: `frontend/`（空目录，Task 7 填充）
- Create: `tests/`（空目录，Task 2 填充）
- Modify: `.gitignore`（整体替换）
- Delete: `index.html`、`public/`、`src/`、`package.json`、`package-lock.json`、`tsconfig.json`、`tsconfig.app.json`、`tsconfig.node.json`、`vite.config.ts`（Vue 代码不迁移，git 历史保留）

**Interfaces:**
- Consumes: 无
- Produces: 干净的仓库根，后续任务在 frontend/backend/tests 下展开

- [ ] **Step 1: 替换 .gitignore**

```gitignore
# Node
node_modules/
dist/
*.local

# .NET
bin/
obj/
.vs/
*.user

# Env
.env
.env.local
.env.*.local
```

- [ ] **Step 2: 删除根级 Vue 文件**

Run: `cd /e/GitHub/OpenSandBoxWeb && git rm -r src public index.html package.json package-lock.json tsconfig.json tsconfig.app.json tsconfig.node.json vite.config.ts`
Expected: 文件从工作区与索引移除

- [ ] **Step 3: 创建目录占位**

Run: `mkdir -p backend frontend tests && touch backend/.gitkeep frontend/.gitkeep tests/.gitkeep`

- [ ] **Step 4: 验证并提交**

Run: `git status`
Expected: `.gitignore` 修改 + 9 个文件删除 + 3 个 .gitkeep 新增

```bash
git add -A
git commit -m "chore: restructure repo skeleton (backend/frontend/tests dirs, remove Vue root files)

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 2: 后端脚手架 + 健康检查 + 测试项目骨架

**Files:**
- Create: `backend/OpenSandbox.Web.csproj`
- Create: `backend/Program.cs`
- Create: `backend/appsettings.json`
- Create: `backend/appsettings.Development.json`
- Create: `backend/Properties/launchSettings.json`
- Create: `backend/Host/Web/HealthEndpoints.cs`
- Create: `backend/Infrastructure/Gateways/OpenSandboxOptions.cs`
- Create: `backend/Infrastructure/DependencyInjection/GatewayRegistration.cs`
- Create: `tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/HealthEndpointsTests.cs`
- Create: `OpenSandboxWeb.slnx`（`dotnet new sln` 生成）
- Delete: `backend/.gitkeep`、`tests/.gitkeep`

**Interfaces:**
- Consumes: 无
- Produces:
  - `OpenSandbox.Web.Host.Web.HealthEndpoints.MapHealthEndpoints(this IEndpointRouteBuilder)` — GET /api/v1/health
  - `OpenSandbox.Web.Infrastructure.Gateways.OpenSandboxOptions`（SectionName、Domain、ApiKey、Protocol、RequestTimeoutSeconds、DisableMetrics、ToConnectionConfigOptions()）
  - `OpenSandbox.Web.Infrastructure.DependencyInjection.GatewayRegistration.AddOpenSandbox(this IServiceCollection, IConfiguration)` — 本任务只注册 options；Task 4 增加 Gateway 单例注册
  - `public partial class Program` — WebApplicationFactory 入口

- [ ] **Step 1: 写后端 csproj**

`backend/OpenSandbox.Web.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Alibaba.OpenSandbox" Version="1.1.1" />
  </ItemGroup>

  <!-- 前端构建产物（frontend/dist）纳入发布目录，wwwroot 下托管；
       dev 阶段 dist 不存在时该 glob 为空，无副作用 -->
  <ItemGroup>
    <Content Include="..\frontend\dist\**" LinkBase="wwwroot"
             CopyToOutputDirectory="PreserveNewest"
             CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写配置与入口**

`backend/appsettings.json`：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "OpenSandbox": {
    "Domain": "",
    "ApiKey": "",
    "Protocol": "https",
    "RequestTimeoutSeconds": 30,
    "DisableMetrics": false
  }
}
```

`backend/appsettings.Development.json`：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Information"
    }
  },
  "OpenSandbox": {
    "Domain": "https://sandbox.example.com",
    "ApiKey": "dev-key"
  }
}
```

`backend/Properties/launchSettings.json`：

```json
{
  "$schema": "http://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5080",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 3: 写 OpenSandboxOptions**

`backend/Infrastructure/Gateways/OpenSandboxOptions.cs`：

```csharp
using OpenSandbox.Config;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public sealed class OpenSandboxOptions
{
    public const string SectionName = "OpenSandbox";

    public string Domain { get; init; } = "";
    public string? ApiKey { get; init; }
    public string Protocol { get; init; } = "https";
    public int RequestTimeoutSeconds { get; init; } = 30;
    public bool DisableMetrics { get; init; }

    public ConnectionConfigOptions ToConnectionConfigOptions() => new()
    {
        Domain = Domain,
        ApiKey = ApiKey,
        Protocol = Enum.TryParse<ConnectionProtocol>(Protocol, ignoreCase: true, out var protocol)
            ? protocol
            : ConnectionProtocol.Https,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        DisableMetrics = DisableMetrics,
    };
}
```

- [ ] **Step 4: 写注册扩展与健康端点**

`backend/Infrastructure/DependencyInjection/GatewayRegistration.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Infrastructure.DependencyInjection;

public static class GatewayRegistration
{
    public static IServiceCollection AddOpenSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenSandboxOptions>(configuration.GetSection(OpenSandboxOptions.SectionName));
        return services;
    }
}
```

`backend/Host/Web/HealthEndpoints.cs`：

```csharp
namespace OpenSandbox.Web.Host.Web;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", () => Results.Ok(new
        {
            status = "ok",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        }));
        return app;
    }
}
```

- [ ] **Step 5: 写 Program.cs**

`backend/Program.cs`：

```csharp
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

app.MapHealthEndpoints();

app.Run();

public partial class Program;
```

- [ ] **Step 6: 验证后端可编译运行**

Run: `cd /e/GitHub/OpenSandBoxWeb/backend && dotnet build`
Expected: Build succeeded, 0 warnings/errors

Run: `dotnet run`（另开终端），然后 `curl http://localhost:5080/api/v1/health`
Expected: `{"status":"ok","version":"1.0.0"}`

- [ ] **Step 7: 创建解决方案与测试项目**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet new sln -n OpenSandboxWeb`

`tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.10" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\backend\OpenSandbox.Web.csproj" />
  </ItemGroup>
</Project>
```

Run: `dotnet sln add backend/OpenSandbox.Web.csproj tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj && git rm backend/.gitkeep tests/.gitkeep`

- [ ] **Step 8: 写 ApiFactory 与健康检查测试**

`tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`：

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeSandboxGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);
        });
    }
}
```

> 说明：`ISandboxGateway`/`FakeSandboxGateway` 在 Task 4 创建。本任务先写 health 测试不依赖 Gateway —— 把 ApiFactory 中两行 Gateway 替换行保留（Task 4 编译通过前该文件暂不编译即可：health 测试用 `WebApplicationFactory<Program>` 直接实例化）。为避免编译失败，本任务 ApiFactory 先写为：

```csharp
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
}
```

Task 4 再替换为带 FakeSandboxGateway 的版本。

`tests/OpenSandbox.Web.Tests/Endpoints/HealthEndpointsTests.cs`：

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class HealthEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsOkStatus()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}
```

- [ ] **Step 9: 运行测试并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 1 passed

```bash
git add -A
git commit -m "feat: add backend scaffold with health endpoint and test project

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 3: SDK 异常翻译 + 中间件

**Files:**
- Create: `backend/Infrastructure/Gateways/SandboxExceptionTranslator.cs`
- Create: `backend/Contracts/ErrorContracts.cs`
- Create: `backend/Host/Web/SandboxExceptionMiddleware.cs`
- Create: `tests/OpenSandbox.Web.Tests/Gateways/SandboxExceptionTranslatorTests.cs`
- Modify: `backend/Program.cs`（挂中间件）

**Interfaces:**
- Consumes: Task 2 的 Program.cs / Options
- Produces:
  - `ErrorResponse(string Code, string Message)` — 统一错误体
  - `SandboxExceptionTranslator.Translate(Exception) → (int StatusCode, ErrorResponse Body)`
  - `SandboxExceptionMiddleware` — 全局 catch，按 Translate 结果写响应

- [ ] **Step 1: 写失败测试**

`tests/OpenSandbox.Web.Tests/Gateways/SandboxExceptionTranslatorTests.cs`：

```csharp
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Gateways;

public sealed class SandboxExceptionTranslatorTests
{
    [Theory]
    [InlineData(typeof(InvalidArgumentException), 400, "invalid_argument")]
    [InlineData(typeof(SandboxUnhealthyException), 503, "sandbox_unhealthy")]
    [InlineData(typeof(SandboxReadyTimeoutException), 504, "sandbox_not_ready")]
    [InlineData(typeof(SandboxInternalException), 500, "sandbox_internal")]
    public void KnownExceptionTypes_MapToExpectedStatusAndCode(Type exceptionType, int expectedStatus, string expectedCode)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom", null)!;

        var (status, body) = SandboxExceptionTranslator.Translate(exception);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body.Code);
    }

    [Fact]
    public void ApiException_WithStatusCode_UsesItsCodeAndMessage()
    {
        var exception = new SandboxApiException("conflict", 409, null, null, null,
            new SandboxError("sandbox_conflict", "already exists"));

        var (status, body) = SandboxExceptionTranslator.Translate(exception);

        Assert.Equal(409, status);
        Assert.Equal("sandbox_conflict", body.Code);
        Assert.Equal("already exists", body.Message);
    }

    [Fact]
    public void UnknownException_MapsToInternalError()
    {
        var (status, body) = SandboxExceptionTranslator.Translate(new InvalidOperationException("boom"));

        Assert.Equal(500, status);
        Assert.Equal("internal_error", body.Code);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 编译失败（SandboxExceptionTranslator、ErrorResponse 不存在）

- [ ] **Step 3: 写 ErrorResponse 与 Translator**

`backend/Contracts/ErrorContracts.cs`：

```csharp
namespace OpenSandbox.Web.Contracts;

public sealed record ErrorResponse(string Code, string Message);
```

`backend/Infrastructure/Gateways/SandboxExceptionTranslator.cs`：

```csharp
using Microsoft.AspNetCore.Http;
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public static class SandboxExceptionTranslator
{
    public static (int StatusCode, ErrorResponse Body) Translate(Exception exception) => exception switch
    {
        InvalidArgumentException => (StatusCodes.Status400BadRequest, new ErrorResponse("invalid_argument", exception.Message)),
        SandboxReadyTimeoutException => (StatusCodes.Status504GatewayTimeout, new ErrorResponse("sandbox_not_ready", exception.Message)),
        SandboxUnhealthyException => (StatusCodes.Status503ServiceUnavailable, new ErrorResponse("sandbox_unhealthy", exception.Message)),
        SandboxInternalException => (StatusCodes.Status500InternalServerError, new ErrorResponse("sandbox_internal", exception.Message)),
        SandboxApiException apiException => TranslateApiException(apiException),
        SandboxException => (StatusCodes.Status502BadGateway, new ErrorResponse("sandbox_error", exception.Message)),
        _ => (StatusCodes.Status500InternalServerError, new ErrorResponse("internal_error", "An unexpected error occurred.")),
    };

    private static (int StatusCode, ErrorResponse Body) TranslateApiException(SandboxApiException exception)
    {
        var statusCode = exception.StatusCode is >= 400 and <= 599
            ? exception.StatusCode.Value
            : StatusCodes.Status502BadGateway;
        var code = exception.Error?.Code ?? "sandbox_api_error";
        var message = exception.Error?.Message ?? exception.Message;
        return (statusCode, new ErrorResponse(code, message));
    }
}
```

- [ ] **Step 4: 写中间件并挂载**

`backend/Host/Web/SandboxExceptionMiddleware.cs`：

```csharp
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
```

Modify `backend/Program.cs`：在 `var app = builder.Build();` 后、`app.MapHealthEndpoints();` 前插入：

```csharp
app.UseMiddleware<SandboxExceptionMiddleware>();
```

- [ ] **Step 5: 运行测试确认通过**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 6 passed（4 theory + 2 fact + health 1 = 7 passed 实际）

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat: add SDK exception translation middleware

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 4: Contracts DTO + Gateway + Mapper

**Files:**
- Create: `backend/Contracts/SandboxContracts.cs`
- Create: `backend/Contracts/ListContracts.cs`
- Create: `backend/Contracts/MetricsContracts.cs`
- Create: `backend/Infrastructure/Gateways/ISandboxGateway.cs`
- Create: `backend/Infrastructure/Gateways/SandboxGateway.cs`
- Create: `backend/Infrastructure/Gateways/SandboxMapper.cs`
- Modify: `backend/Infrastructure/DependencyInjection/GatewayRegistration.cs`（补 Gateway 注册）
- Create: `tests/OpenSandbox.Web.Tests/Gateways/SandboxMapperTests.cs`
- Modify: `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`（替换为带 Fake 版本）
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/FakeSandboxGateway.cs`

**Interfaces:**
- Consumes: Task 2 的 Options、Task 3 的 ErrorResponse
- Produces:
  - 全部 Contracts DTO（Task 5/6 端点与前端契约依据）
  - `ISandboxGateway`（9 个方法，签名见下）—— Task 5/6 端点依赖
  - `SandboxMapper.ToSdk/ToDto/ToCreateResponse/ToMetricsDto`

- [ ] **Step 1: 写 DTO 契约文件**

`backend/Contracts/SandboxContracts.cs`：

```csharp
namespace OpenSandbox.Web.Contracts;

public sealed record ImageAuthDto(string Username, string Password);

public sealed record ImageRefDto(string Uri, ImageAuthDto? Auth = null);

public sealed record SandboxStatusDto(string State, string? Reason = null, string? Message = null);

public sealed record SandboxDto(
    string Id,
    ImageRefDto Image,
    SandboxStatusDto Status,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<string> Entrypoint,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public sealed record NetworkRuleDto(string Action, string Target);

public sealed record NetworkPolicyDto(string? DefaultAction, IReadOnlyList<NetworkRuleDto>? Egress);

public sealed record VolumeDto(
    string Name,
    string MountPath,
    bool? ReadOnly = null,
    string? SubPath = null,
    string? HostPath = null,
    string? PvcClaimName = null);

public sealed record CreateSandboxRequest(
    ImageRefDto? Image,
    int? Timeout,
    Dictionary<string, string>? ResourceLimits,
    IReadOnlyList<string>? Entrypoint,
    Dictionary<string, string>? Env = null,
    Dictionary<string, string>? Metadata = null,
    NetworkPolicyDto? NetworkPolicy = null,
    IReadOnlyList<VolumeDto>? Volumes = null,
    Dictionary<string, string>? Extensions = null);

public sealed record CreateSandboxResponse(
    string Id,
    SandboxStatusDto Status,
    Dictionary<string, string>? Metadata,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    IReadOnlyList<string> Entrypoint);

public sealed record RenewExpirationRequest(string? ExpiresAt);

public sealed record RenewExpirationResponse(DateTime? ExpiresAt);

public sealed record EndpointDto(string Endpoint, IReadOnlyDictionary<string, string>? Headers);
```

`backend/Contracts/ListContracts.cs`：

```csharp
namespace OpenSandbox.Web.Contracts;

public sealed record PaginationInfoDto(int Page, int PageSize, int TotalItems, int TotalPages, bool HasNextPage);

public sealed record ListSandboxesResponse(IReadOnlyList<SandboxDto> Items, PaginationInfoDto Pagination);
```

`backend/Contracts/MetricsContracts.cs`：

```csharp
namespace OpenSandbox.Web.Contracts;

public sealed record MetricsDto(double CpuCount, double CpuUsedPct, double MemTotalMib, double MemUsedMib, long Timestamp);
```

- [ ] **Step 2: 写 Gateway 接口与实现**

`backend/Infrastructure/Gateways/ISandboxGateway.cs`：

```csharp
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public interface ISandboxGateway
{
    Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default);
    Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<SandboxDto> GetAsync(string id, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task PauseAsync(string id, CancellationToken ct = default);
    Task ResumeAsync(string id, CancellationToken ct = default);
    Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default);
    Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default);
    Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default);
}
```

`backend/Infrastructure/Gateways/SandboxMapper.cs`：

```csharp
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using Models = OpenSandbox.Models;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public static class SandboxMapper
{
    public static Models.CreateSandboxRequest ToSdk(CreateSandboxRequest request)
    {
        if (request.Image is null || string.IsNullOrWhiteSpace(request.Image.Uri))
            throw new InvalidArgumentException("image.uri is required", null);
        if (request.Entrypoint is null || request.Entrypoint.Count == 0)
            throw new InvalidArgumentException("entrypoint is required", null);

        return new Models.CreateSandboxRequest
        {
            Image = new Models.ImageSpec
            {
                Uri = request.Image.Uri,
                Auth = request.Image.Auth is null ? null : new Models.ImageAuth
                {
                    Username = request.Image.Auth.Username,
                    Password = request.Image.Auth.Password,
                },
            },
            Entrypoint = request.Entrypoint,
            Timeout = request.Timeout,
            ResourceLimits = request.ResourceLimits,
            Env = request.Env,
            Metadata = request.Metadata,
            NetworkPolicy = request.NetworkPolicy is null ? null : new Models.NetworkPolicy
            {
                DefaultAction = string.IsNullOrEmpty(request.NetworkPolicy.DefaultAction)
                    ? null
                    : Enum.Parse<Models.NetworkRuleAction>(request.NetworkPolicy.DefaultAction, ignoreCase: true),
                Egress = request.NetworkPolicy.Egress?.Select(rule => new Models.NetworkRule
                {
                    Action = Enum.Parse<Models.NetworkRuleAction>(rule.Action, ignoreCase: true),
                    Target = rule.Target,
                }).ToList(),
            },
            Volumes = request.Volumes?.Select(volume => new Models.Volume
            {
                Name = volume.Name,
                MountPath = volume.MountPath,
                ReadOnly = volume.ReadOnly,
                SubPath = volume.SubPath,
                Host = volume.HostPath is null ? null : new Models.Host { Path = volume.HostPath },
                Pvc = volume.PvcClaimName is null ? null : new Models.PVC { ClaimName = volume.PvcClaimName },
            }).ToList(),
            Extensions = request.Extensions,
        };
    }

    public static SandboxDto ToDto(Models.SandboxInfo info) => new(
        info.Id,
        new ImageRefDto(
            info.Image.Uri,
            info.Image.Auth is null ? null : new ImageAuthDto(info.Image.Auth.Username, info.Image.Auth.Password)),
        new SandboxStatusDto(info.Status.State, info.Status.Reason, info.Status.Message),
        info.Metadata,
        info.Entrypoint,
        info.ExpiresAt,
        info.CreatedAt);

    public static CreateSandboxResponse ToCreateResponse(Models.CreateSandboxResponse response) => new(
        response.Id,
        new SandboxStatusDto(response.Status.State, response.Status.Reason, response.Status.Message),
        response.Metadata,
        response.ExpiresAt,
        response.CreatedAt,
        response.Entrypoint);

    public static MetricsDto ToMetricsDto(Models.SandboxMetrics metrics) => new(
        metrics.CpuCount,
        metrics.CpuUsedPercentage,
        metrics.MemoryTotalMiB,
        metrics.MemoryUsedMiB,
        metrics.Timestamp);
}
```

`backend/Infrastructure/Gateways/SandboxGateway.cs`：

```csharp
using Microsoft.Extensions.Options;
using OpenSandbox.Config;
using OpenSandbox.Factory;
using OpenSandbox.Services;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public sealed class SandboxGateway : ISandboxGateway
{
    private const int ExecdPort = 44772;

    private readonly ConnectionConfig _connectionConfig;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<DefaultAdapterFactory> _factory = new(() => new DefaultAdapterFactory());
    private readonly Lazy<LifecycleStack> _lifecycle;

    public SandboxGateway(IOptions<OpenSandboxOptions> options, ILoggerFactory loggerFactory)
    {
        _connectionConfig = new ConnectionConfig(options.Value.ToConnectionConfigOptions());
        _loggerFactory = loggerFactory;
        _lifecycle = new Lazy<LifecycleStack>(CreateLifecycleStack);
    }

    private ISandboxes Sandboxes => _lifecycle.Value.Sandboxes;

    private LifecycleStack CreateLifecycleStack() =>
        _factory.Value.CreateLifecycleStack(new CreateLifecycleStackOptions
        {
            ConnectionConfig = _connectionConfig,
            LoggerFactory = _loggerFactory,
        });

    public async Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default)
    {
        var created = await Sandboxes.CreateSandboxAsync(SandboxMapper.ToSdk(request), ct);
        return SandboxMapper.ToCreateResponse(created);
    }

    public async Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var result = await Sandboxes.ListSandboxesAsync(new OpenSandbox.Models.ListSandboxesParams
        {
            States = states,
            Metadata = metadata,
            Page = page,
            PageSize = pageSize,
        }, ct);
        return new ListSandboxesResponse(
            result.Items.Select(SandboxMapper.ToDto).ToList(),
            new PaginationInfoDto(
                result.Pagination.Page,
                result.Pagination.PageSize,
                result.Pagination.TotalItems,
                result.Pagination.TotalPages,
                result.Pagination.HasNextPage));
    }

    public async Task<SandboxDto> GetAsync(string id, CancellationToken ct = default)
        => SandboxMapper.ToDto(await Sandboxes.GetSandboxAsync(id, ct));

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => Sandboxes.DeleteSandboxAsync(id, ct);

    public Task PauseAsync(string id, CancellationToken ct = default)
        => Sandboxes.PauseSandboxAsync(id, ct);

    public Task ResumeAsync(string id, CancellationToken ct = default)
        => Sandboxes.ResumeSandboxAsync(id, ct);

    public async Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default)
    {
        var renewed = await Sandboxes.RenewSandboxExpirationAsync(
            id,
            new OpenSandbox.Models.RenewSandboxExpirationRequest { ExpiresAt = request.ExpiresAt! },
            ct);
        return new RenewExpirationResponse(renewed.ExpiresAt);
    }

    public async Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default)
    {
        var endpoint = await Sandboxes.GetSandboxEndpointAsync(id, port, false, ct);
        return new EndpointDto(endpoint.EndpointAddress, endpoint.Headers);
    }

    public async Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default)
    {
        var endpoint = await Sandboxes.GetSandboxEndpointAsync(id, ExecdPort, false, ct);
        var execd = _factory.Value.CreateExecdStack(new CreateExecdStackOptions
        {
            ConnectionConfig = _connectionConfig,
            ExecdBaseUrl = $"http://{endpoint.EndpointAddress}",
            LoggerFactory = _loggerFactory,
        });
        return SandboxMapper.ToMetricsDto(await execd.Metrics.GetMetricsAsync(ct));
    }
}
```

- [ ] **Step 3: 补 Gateway 注册**

Modify `backend/Infrastructure/DependencyInjection/GatewayRegistration.cs`，AddOpenSandbox 方法体改为：

```csharp
    public static IServiceCollection AddOpenSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenSandboxOptions>(configuration.GetSection(OpenSandboxOptions.SectionName));
        services.AddSingleton<ISandboxGateway, SandboxGateway>();
        return services;
    }
```

- [ ] **Step 4: 写 Mapper 测试**

`tests/OpenSandbox.Web.Tests/Gateways/SandboxMapperTests.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;
using Models = OpenSandbox.Models;

namespace OpenSandbox.Web.Tests.Gateways;

public sealed class SandboxMapperTests
{
    [Fact]
    public void ToDto_MapsSandboxInfo()
    {
        var info = new Models.SandboxInfo
        {
            Id = "sb-1",
            Image = new Models.ImageSpec { Uri = "ubuntu:24.04" },
            Status = new Models.SandboxStatus { State = "Running" },
            Entrypoint = ["/bin/sh"],
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var dto = SandboxMapper.ToDto(info);

        Assert.Equal("sb-1", dto.Id);
        Assert.Equal("ubuntu:24.04", dto.Image.Uri);
        Assert.Equal("Running", dto.Status.State);
        Assert.Equal("/bin/sh", Assert.Single(dto.Entrypoint));
    }

    [Fact]
    public void ToSdk_MapsNetworkPolicyAndVolumes()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("ubuntu:24.04"),
            300,
            new Dictionary<string, string> { ["cpu"] = "500m" },
            ["/bin/sh"],
            null,
            null,
            new NetworkPolicyDto("deny", [new NetworkRuleDto("allow", "10.0.0.0/8")]),
            [new VolumeDto("data", "/data", false, null, "/tmp/data", null)],
            null);

        var sdk = SandboxMapper.ToSdk(request);

        Assert.Equal(Models.NetworkRuleAction.Deny, sdk.NetworkPolicy!.DefaultAction);
        Assert.Equal(Models.NetworkRuleAction.Allow, sdk.NetworkPolicy.Egress![0].Action);
        Assert.Equal("/tmp/data", sdk.Volumes![0].Host!.Path);
    }

    [Fact]
    public void ToSdk_MissingImageUri_ThrowsInvalidArgument()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("  "),
            null,
            null,
            ["/bin/sh"],
            null, null, null, null, null);

        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(() => SandboxMapper.ToSdk(request));

        Assert.Contains("image.uri", exception.Message);
    }
}
```

- [ ] **Step 5: 写 FakeSandboxGateway 并更新 ApiFactory**

`tests/OpenSandbox.Web.Tests/Endpoints/FakeSandboxGateway.cs`：

```csharp
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class FakeSandboxGateway : ISandboxGateway
{
    public List<SandboxDto> Sandboxes { get; } =
    [
        new SandboxDto(
            "sb-1",
            new ImageRefDto("ubuntu:24.04"),
            new SandboxStatusDto("Running"),
            new Dictionary<string, string> { ["name"] = "demo" },
            ["/bin/sh"],
            null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
    ];

    public Exception? NextException { get; set; }

    private Exception TakeException()
    {
        var exception = NextException!;
        NextException = null;
        return exception;
    }

    public Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new CreateSandboxResponse(
            "sb-new",
            new SandboxStatusDto("Pending"),
            request.Metadata,
            null,
            DateTime.UtcNow,
            request.Entrypoint ?? []));
    }

    public Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        var filtered = Sandboxes
            .Where(s => states is null || states.Count == 0 || states.Contains(s.Status.State))
            .ToList();
        var total = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new ListSandboxesResponse(
            items,
            new PaginationInfoDto(page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize), page * pageSize < total)));
    }

    public Task<SandboxDto> GetAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        var sandbox = Sandboxes.FirstOrDefault(s => s.Id == id);
        if (sandbox is null)
            throw new SandboxApiException("not found", 404, null, null, null, new SandboxError("not_found", $"sandbox {id} not found"));
        return Task.FromResult(sandbox);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task PauseAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new RenewExpirationResponse(DateTime.Parse(request.ExpiresAt!)));
    }

    public Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new EndpointDto($"sandbox-{id}:{port}", null));
    }

    public Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new MetricsDto(4, 12.5, 4096, 1024, 1700000000000));
    }
}
```

Modify `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`（整体替换为带 Fake 的版本，即 Task 2 中注释前的完整版本）：

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeSandboxGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);
        });
    }
}
```

- [ ] **Step 6: 运行全部测试**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 全部通过（health 1 + translator 7 + mapper 3 = 11 passed）

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat: add contracts DTOs, sandbox gateway and SDK mappers

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 5: 沙箱管理端点（CRUD + 生命周期）

**Files:**
- Create: `backend/Host/Web/SandboxEndpoints.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/SandboxEndpointsTests.cs`
- Modify: `backend/Program.cs`（挂 MapSandboxEndpoints）

**Interfaces:**
- Consumes: Task 4 的 ISandboxGateway、Contracts、FakeSandboxGateway、ApiFactory
- Produces: `/api/v1/sandboxes` 全部 8 条路由（前端 API 层依据）

- [ ] **Step 1: 写端点文件**

`backend/Host/Web/SandboxEndpoints.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Host.Web;

public static class SandboxEndpoints
{
    public static IEndpointRouteBuilder MapSandboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sandboxes");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/pause", PauseAsync);
        group.MapPost("/{id}/resume", ResumeAsync);
        group.MapPost("/{id}/renew-expiration", RenewExpirationAsync);
        group.MapGet("/{id}/endpoints/{port:int}", GetEndpointAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ISandboxGateway gateway,
        string[]? state,
        string[]? metadata,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await gateway.ListAsync(
            state is null || state.Length == 0 ? null : state,
            ParseMetadata(metadata),
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, 100),
            ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(CreateSandboxRequest request, ISandboxGateway gateway, CancellationToken ct)
    {
        if (request is null || request.Image is null || string.IsNullOrWhiteSpace(request.Image.Uri))
            return Results.BadRequest(new ErrorResponse("invalid_argument", "image.uri is required"));
        if (request.Entrypoint is null || request.Entrypoint.Count == 0)
            return Results.BadRequest(new ErrorResponse("invalid_argument", "entrypoint is required"));

        var created = await gateway.CreateAsync(request, ct);
        return Results.Created($"/api/v1/sandboxes/{created.Id}", created);
    }

    private static async Task<IResult> GetAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetAsync(id, ct));

    private static async Task<IResult> DeleteAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PauseAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.PauseAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.ResumeAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RenewExpirationAsync(string id, RenewExpirationRequest request, ISandboxGateway gateway, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ExpiresAt))
            return Results.BadRequest(new ErrorResponse("invalid_argument", "expiresAt is required"));

        var renewed = await gateway.RenewExpirationAsync(id, request, ct);
        return Results.Ok(renewed);
    }

    private static async Task<IResult> GetEndpointAsync(string id, int port, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetEndpointAsync(id, port, ct));

    private static Dictionary<string, string>? ParseMetadata(string[]? metadata)
    {
        if (metadata is null || metadata.Length == 0) return null;

        var result = new Dictionary<string, string>();
        foreach (var entry in metadata)
        {
            var separator = entry.IndexOf('=');
            if (separator > 0)
                result[entry[..separator]] = entry[(separator + 1)..];
        }
        return result;
    }
}
```

- [ ] **Step 2: 挂载端点**

Modify `backend/Program.cs`：在 `app.MapHealthEndpoints();` 后加一行：

```csharp
app.MapSandboxEndpoints();
```

- [ ] **Step 3: 写 E2E 测试**

`tests/OpenSandbox.Web.Tests/Endpoints/SandboxEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class SandboxEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SandboxEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_ReturnsSeededSandbox()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/sandboxes?page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListSandboxesResponse>();
        Assert.Single(body!.Items);
        Assert.Equal("sb-1", body.Items[0].Id);
        Assert.Equal(1, body.Pagination.TotalItems);
    }

    [Fact]
    public async Task Create_WithoutImageUri_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/sandboxes", new { entrypoint = new[] { "/bin/sh" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_argument", body!.Code);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithSandbox()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            image = new { uri = "ubuntu:24.04" },
            entrypoint = new[] { "/bin/sh" },
            resourceLimits = new { cpu = "500m", memory = "512Mi" },
            timeout = 300,
            env = new { FOO = "bar" },
        };

        var response = await client.PostAsJsonAsync("/api/v1/sandboxes", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateSandboxResponse>();
        Assert.Equal("sb-new", created!.Id);
        Assert.Equal("Pending", created.Status.State);
    }

    [Fact]
    public async Task Get_MissingSandbox_Returns404WithErrorBody()
    {
        _factory.Gateway.NextException = new SandboxApiException(
            "not found", 404, null, null, null, new SandboxError("not_found", "sandbox missing not found"));

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/sandboxes/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("not_found", body!.Code);
    }

    [Fact]
    public async Task Pause_Returns204()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/sandboxes/sb-1/pause", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RenewExpiration_WithoutExpiresAt_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/sandboxes/sb-1/renew-expiration", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 全部通过（新增 6 passed）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat: add sandbox management endpoints with E2E tests

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 6: 指标端点（快照 + SSE）

**Files:**
- Create: `backend/Host/Web/MetricsEndpoints.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/MetricsEndpointsTests.cs`
- Modify: `backend/Program.cs`（挂 MapMetricsEndpoints）

**Interfaces:**
- Consumes: Task 4 的 ISandboxGateway.GetMetricsAsync、MetricsDto；Task 5 的测试设施
- Produces: `GET /api/v1/sandboxes/{id}/metrics`（JSON 快照）与 `GET /api/v1/sandboxes/{id}/metrics/watch`（SSE：`data: {"type":"connected"}` → 每 2s `data: {"type":"metrics","data":{...}}`；出错发 `data: {"type":"error","error":"..."}` 后关闭）

- [ ] **Step 1: 写端点文件**

`backend/Host/Web/MetricsEndpoints.cs`：

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Host.Web;

public static class MetricsEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sandboxes/{id}/metrics", GetSnapshotAsync);
        app.MapGet("/api/v1/sandboxes/{id}/metrics/watch", WatchAsync);
        return app;
    }

    private static async Task<IResult> GetSnapshotAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetMetricsAsync(id, ct));

    private static IResult WatchAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => TypedResults.ServerSentEvents(StreamMetrics(id, gateway, ct));

    private static async IAsyncEnumerable<SseItem<string>> StreamMetrics(
        string id,
        ISandboxGateway gateway,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "connected" }, WebJson));

        while (!ct.IsCancellationRequested)
        {
            MetricsDto metrics;
            try
            {
                metrics = await gateway.GetMetricsAsync(id, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception exception)
            {
                yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "error", error = exception.Message }, WebJson));
                yield break;
            }

            yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "metrics", data = metrics }, WebJson));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
```

- [ ] **Step 2: 挂载端点**

Modify `backend/Program.cs`：在 `app.MapSandboxEndpoints();` 后加一行：

```csharp
app.MapMetricsEndpoints();
```

- [ ] **Step 3: 写测试**

`tests/OpenSandbox.Web.Tests/Endpoints/MetricsEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class MetricsEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MetricsEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Snapshot_ReturnsMetrics()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/sandboxes/sb-1/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MetricsDto>();
        Assert.Equal(4, body!.CpuCount);
        Assert.Equal(12.5, body.CpuUsedPct);
    }

    [Fact]
    public async Task Watch_StreamsConnectedEventAsSse()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sandboxes/sb-1/metrics/watch");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await reader.ReadLineAsync(cts.Token);

        Assert.StartsWith("data: ", line);
        Assert.Contains("\"type\":\"connected\"", line);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 全部通过（新增 2 passed）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat: add metrics snapshot and SSE streaming endpoints

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 7: 前端脚手架（Vite + React + 依赖 + 配置）

**Files:**
- Create: `frontend/` 全部脚手架文件（`npm create vite` 生成后按下方清单改写）
- Create: `frontend/src/styles/global.css`
- Create: `frontend/src/test/setup.ts`
- Modify: `frontend/vite.config.ts`（整体替换）
- Modify: `frontend/tsconfig.app.json`（加 alias）
- Modify: `frontend/package.json`（scripts + 依赖）
- Delete: `frontend/.gitkeep`；模板自带的 `src/App.css`、`src/index.css`、`src/assets/react.svg`、`public/vite.svg`、`eslint.config.js`

**Interfaces:**
- Consumes: 无（Task 8+ 在前端目录展开）
- Produces: dev server（3000，`/api` → 5080）、`npm run build`（dist）、`npm test`（vitest）、`npm run lint`（oxlint）、`@` alias → `src/`

- [ ] **Step 1: 生成模板**

Run: `cd /e/GitHub/OpenSandBoxWeb && npm create vite@latest frontend -- --template react-ts && cd frontend && npm install`
Expected: 生成 React+TS 模板并安装依赖

- [ ] **Step 2: 安装依赖并移除 eslint**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm i antd @ant-design/icons @tanstack/react-query axios dayjs echarts react-router-dom && npm i -D vitest jsdom @testing-library/react @testing-library/jest-dom @testing-library/user-event oxlint && npm rm eslint @eslint/js eslint-plugin-react-hooks eslint-plugin-react-refresh globals typescript-eslint && rm eslint.config.js src/App.css src/index.css src/assets/react.svg public/vite.svg`

- [ ] **Step 3: 改写 package.json scripts**

Modify `frontend/package.json`：scripts 段替换为：

```json
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "lint": "oxlint",
    "preview": "vite preview",
    "test": "vitest run",
    "test:watch": "vitest"
  },
```

- [ ] **Step 4: 改写 vite.config.ts**

`frontend/vite.config.ts` 整体替换：

```ts
/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'path'

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
    },
  },
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('antd') || id.includes('@ant-design')) {
            return 'antd'
          }
          if (id.includes('echarts')) {
            return 'echarts'
          }
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    globals: true,
    css: false,
  },
})
```

- [ ] **Step 5: 加 tsconfig alias**

Modify `frontend/tsconfig.app.json`：`compilerOptions` 中加：

```json
    "baseUrl": ".",
    "paths": {
      "@/*": ["./src/*"]
    }
```

- [ ] **Step 6: 写测试 setup 与全局样式**

`frontend/src/test/setup.ts`：

```ts
import '@testing-library/jest-dom/vitest'

// antd 组件在 jsdom 下需要 matchMedia
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
})

// echarts 需要 ResizeObserver
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
window.ResizeObserver = window.ResizeObserver ?? (ResizeObserverStub as unknown as typeof ResizeObserver)
```

`frontend/src/styles/global.css`：

```css
html,
body,
#root {
  margin: 0;
  padding: 0;
  min-height: 100vh;
}

body {
  font-family:
    -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue',
    Arial, 'PingFang SC', 'Microsoft YaHei', sans-serif;
}
```

- [ ] **Step 7: 清空模板 App 占位**

Modify `frontend/src/App.tsx` 整体替换：

```tsx
export default function App() {
  return null
}
```

Modify `frontend/index.html`：`<title>` 改为 `OpenSandbox Admin`，`lang` 改为 `zh-CN`。

- [ ] **Step 8: 验证并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm run lint && npm run build && npm test -- --passWithNoTests`
Expected: lint 无错误、build 成功、vitest 无测试文件时通过（--passWithNoTests）

Run: `npm run dev` 验证 http://localhost:3000 可访问后停止

```bash
git add -A
git commit -m "feat: scaffold React frontend with Vite, Ant Design and vitest

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 8: 前端 API 层 + 工具函数 + 单测

**Files:**
- Create: `frontend/src/api/client.ts`
- Create: `frontend/src/api/types.ts`
- Create: `frontend/src/api/sandbox.ts`
- Create: `frontend/src/api/metrics.ts`
- Create: `frontend/src/hooks/usePolling.ts`
- Create: `frontend/src/hooks/useMetricsStream.ts`
- Create: `frontend/src/utils/constants.ts`
- Create: `frontend/src/utils/format.ts`
- Create: `frontend/src/utils/validators.ts`
- Create: `frontend/src/utils/batchCreate.ts`
- Create: `frontend/src/utils/__tests__/validators.test.ts`
- Create: `frontend/src/utils/__tests__/batchCreate.test.ts`

**Interfaces:**
- Consumes: Task 7 脚手架
- Produces（后续页面任务全部依赖，签名以本任务为准）:
  - `apiClient`（axios 实例，baseURL `/api/v1`）、`extractErrorMessage(error): string`
  - `sandboxApi.{create,list,get,remove,pause,resume,renewExpiration,getEndpoint}`
  - `metricsApi.getSnapshot(id)`
  - `usePolling<T>(key: string[], fetcher: () => Promise<T>, intervalMs: number, enabled?, options?)`
  - `useMetricsStream(sandboxId, onEvent, enabled?) → status`
  - `batchCreateSandboxes(items, options?) → {successful, failed}`；options：`{concurrency?, onProgress?, createFn?}`
  - `validateImageUri/validateCpu/validateMemory/validateTimeout → string | null`（null = 合法）
  - `formatTime(value?)`；常量 `EXECD_PORT/DEFAULT_PAGE_SIZE/LIST_REFRESH_INTERVAL_MS/DETAIL_REFRESH_INTERVAL_MS/METRICS_HISTORY_LIMIT/STORAGE_KEY_REFRESH_INTERVAL`

- [ ] **Step 1: 写 API 客户端与类型**

`frontend/src/api/client.ts`：

```ts
import axios from 'axios'

export const apiClient = axios.create({
  baseURL: '/api/v1',
  timeout: 15000,
})

export function extractErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error) && error.response?.data) {
    const data = error.response.data as { message?: string }
    if (data.message) return data.message
  }
  return error instanceof Error ? error.message : 'Unknown error'
}
```

`frontend/src/api/types.ts`：

```ts
export interface ImageAuth {
  username: string
  password: string
}

export interface ImageRef {
  uri: string
  auth?: ImageAuth
}

export interface SandboxStatus {
  state: string
  reason?: string
  message?: string
}

export interface Sandbox {
  id: string
  image: ImageRef
  status: SandboxStatus
  metadata?: Record<string, string>
  entrypoint: string[]
  expiresAt?: string
  createdAt: string
}

export interface NetworkRule {
  action: string
  target: string
}

export interface NetworkPolicy {
  defaultAction?: string
  egress?: NetworkRule[]
}

export interface VolumeSpec {
  name: string
  mountPath: string
  readOnly?: boolean
  subPath?: string
  hostPath?: string
  pvcClaimName?: string
}

export interface CreateSandboxRequest {
  image: ImageRef
  timeout?: number | null
  resourceLimits: Record<string, string>
  entrypoint: string[]
  env?: Record<string, string>
  metadata?: Record<string, string>
  networkPolicy?: NetworkPolicy
  volumes?: VolumeSpec[]
  extensions?: Record<string, string>
}

export interface CreateSandboxResponse {
  id: string
  status: SandboxStatus
  metadata?: Record<string, string>
  expiresAt?: string
  createdAt: string
  entrypoint: string[]
}

export interface ListSandboxesParams {
  state?: string[]
  metadata?: Record<string, string>
  page?: number
  pageSize?: number
}

export interface PaginationInfo {
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
  hasNextPage: boolean
}

export interface ListSandboxesResponse {
  items: Sandbox[]
  pagination: PaginationInfo
}

export interface RenewExpirationRequest {
  expiresAt: string
}

export interface RenewExpirationResponse {
  expiresAt?: string
}

export interface Endpoint {
  endpoint: string
  headers?: Record<string, string>
}

export interface Metrics {
  cpuCount: number
  cpuUsedPct: number
  memTotalMib: number
  memUsedMib: number
  timestamp: number
}

export interface MetricsPoint {
  timestamp: number
  cpu: number
  memory: number
}

export interface ErrorResponse {
  code: string
  message: string
}
```

- [ ] **Step 2: 写 API 方法**

`frontend/src/api/sandbox.ts`：

```ts
import { apiClient } from './client'
import type {
  CreateSandboxRequest,
  CreateSandboxResponse,
  Endpoint,
  ListSandboxesParams,
  ListSandboxesResponse,
  RenewExpirationRequest,
  RenewExpirationResponse,
  Sandbox,
} from './types'

export const sandboxApi = {
  async create(data: CreateSandboxRequest): Promise<CreateSandboxResponse> {
    const response = await apiClient.post<CreateSandboxResponse>('/sandboxes', data)
    return response.data
  },

  async list(params?: ListSandboxesParams): Promise<ListSandboxesResponse> {
    const query = new URLSearchParams()
    params?.state?.forEach((s) => query.append('state', s))
    Object.entries(params?.metadata ?? {}).forEach(([key, value]) =>
      query.append('metadata', `${key}=${value}`),
    )
    if (params?.page) query.append('page', String(params.page))
    if (params?.pageSize) query.append('pageSize', String(params.pageSize))
    const response = await apiClient.get<ListSandboxesResponse>(
      `/sandboxes?${query.toString()}`,
    )
    return response.data
  },

  async get(id: string): Promise<Sandbox> {
    const response = await apiClient.get<Sandbox>(`/sandboxes/${id}`)
    return response.data
  },

  async remove(id: string): Promise<void> {
    await apiClient.delete(`/sandboxes/${id}`)
  },

  async pause(id: string): Promise<void> {
    await apiClient.post(`/sandboxes/${id}/pause`)
  },

  async resume(id: string): Promise<void> {
    await apiClient.post(`/sandboxes/${id}/resume`)
  },

  async renewExpiration(
    id: string,
    data: RenewExpirationRequest,
  ): Promise<RenewExpirationResponse> {
    const response = await apiClient.post<RenewExpirationResponse>(
      `/sandboxes/${id}/renew-expiration`,
      data,
    )
    return response.data
  },

  async getEndpoint(id: string, port: number): Promise<Endpoint> {
    const response = await apiClient.get<Endpoint>(`/sandboxes/${id}/endpoints/${port}`)
    return response.data
  },
}
```

`frontend/src/api/metrics.ts`：

```ts
import { apiClient } from './client'
import type { Metrics } from './types'

export const metricsApi = {
  async getSnapshot(id: string): Promise<Metrics> {
    const response = await apiClient.get<Metrics>(`/sandboxes/${id}/metrics`)
    return response.data
  },
}
```

- [ ] **Step 3: 写 hooks**

`frontend/src/hooks/usePolling.ts`：

```ts
import { useQuery, type UseQueryOptions } from '@tanstack/react-query'

export function usePolling<T>(
  key: string[],
  fetcher: () => Promise<T>,
  intervalMs: number,
  enabled = true,
  options?: Omit<UseQueryOptions<T>, 'queryKey' | 'queryFn'>,
) {
  return useQuery<T>({
    queryKey: key,
    queryFn: fetcher,
    refetchInterval: intervalMs,
    enabled,
    ...options,
  })
}
```

`frontend/src/hooks/useMetricsStream.ts`：

```ts
import { useEffect, useRef, useState } from 'react'
import type { Metrics } from '@/api/types'

export interface MetricsEvent {
  type: 'connected' | 'metrics' | 'error'
  data?: Metrics
  error?: string
}

export function useMetricsStream(
  sandboxId: string,
  onEvent: (event: MetricsEvent) => void,
  enabled = true,
) {
  const [status, setStatus] = useState<'idle' | 'connecting' | 'connected' | 'error'>('idle')
  const onEventRef = useRef(onEvent)
  onEventRef.current = onEvent

  useEffect(() => {
    if (!enabled || !sandboxId) return
    const controller = new AbortController()
    setStatus('connecting')

    ;(async () => {
      try {
        const response = await fetch(`/api/v1/sandboxes/${sandboxId}/metrics/watch`, {
          signal: controller.signal,
        })
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`)
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        setStatus('connected')
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })
          const parts = buffer.split('\n\n')
          buffer = parts.pop() ?? ''
          for (const part of parts) {
            const line = part.split('\n').find((l) => l.startsWith('data: '))
            if (!line) continue
            onEventRef.current(JSON.parse(line.slice(6)))
          }
        }
      } catch (err) {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setStatus('error')
          onEventRef.current({
            type: 'error',
            error: err instanceof Error ? err.message : 'stream failed',
          })
        }
      }
    })()

    return () => controller.abort()
  }, [sandboxId, enabled])

  return status
}
```

- [ ] **Step 4: 写工具函数**

`frontend/src/utils/constants.ts`：

```ts
export const EXECD_PORT = 44772
export const DEFAULT_PAGE_SIZE = 20
export const LIST_REFRESH_INTERVAL_MS = 30000
export const DETAIL_REFRESH_INTERVAL_MS = 10000
export const METRICS_HISTORY_LIMIT = 300
export const STORAGE_KEY_REFRESH_INTERVAL = 'sandbox-refresh-interval'
```

`frontend/src/utils/format.ts`：

```ts
import dayjs from 'dayjs'

export function formatTime(value?: string | null): string {
  if (!value) return '-'
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : String(value)
}
```

`frontend/src/utils/validators.ts`：

```ts
export function validateImageUri(uri: string): string | null {
  if (!uri.trim()) return '镜像地址不能为空'
  if (!/^[a-z0-9][a-z0-9._/-]*(:[a-zA-Z0-9._-]+)?(@sha256:[a-f0-9]{64})?$/i.test(uri.trim())) {
    return '镜像地址格式无效（如 ubuntu:24.04 或 registry.example.com/repo/img:tag）'
  }
  return null
}

export function validateCpu(cpu: string): string | null {
  if (!cpu.trim()) return null
  if (!/^\d+(\.\d+)?m?$/.test(cpu.trim())) return 'CPU 配额格式无效（如 500m 或 1.5）'
  return null
}

export function validateMemory(memory: string): string | null {
  if (!memory.trim()) return null
  if (!/^\d+(\.\d+)?(Ki|Mi|Gi|Ti)?$/i.test(memory.trim())) {
    return '内存配额格式无效（如 512Mi 或 2Gi）'
  }
  return null
}

export function validateTimeout(timeout: number | undefined): string | null {
  if (timeout !== undefined && (Number.isNaN(timeout) || timeout <= 0)) {
    return '超时时间必须大于 0'
  }
  return null
}
```

`frontend/src/utils/batchCreate.ts`：

```ts
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { CreateSandboxRequest, CreateSandboxResponse } from '@/api/types'

export interface BatchCreateOptions {
  concurrency?: number
  onProgress?: (completed: number, total: number) => void
  createFn?: (item: CreateSandboxRequest) => Promise<CreateSandboxResponse>
}

export interface BatchResult {
  successful: { request: CreateSandboxRequest; id: string }[]
  failed: { request: CreateSandboxRequest; error: string }[]
}

export async function batchCreateSandboxes(
  items: CreateSandboxRequest[],
  options?: BatchCreateOptions,
): Promise<BatchResult> {
  const concurrency = options?.concurrency ?? 3
  const createFn = options?.createFn ?? sandboxApi.create
  const successful: BatchResult['successful'] = []
  const failed: BatchResult['failed'] = []
  let completed = 0

  for (let i = 0; i < items.length; i += concurrency) {
    const batch = items.slice(i, i + concurrency)
    const results = await Promise.allSettled(batch.map((item) => createFn(item)))
    results.forEach((result, index) => {
      if (result.status === 'fulfilled') {
        successful.push({ request: batch[index], id: result.value.id })
      } else {
        failed.push({ request: batch[index], error: extractErrorMessage(result.reason) })
      }
      completed += 1
      options?.onProgress?.(completed, items.length)
    })
  }

  return { successful, failed }
}
```

- [ ] **Step 5: 写失败测试**

`frontend/src/utils/__tests__/validators.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { validateCpu, validateImageUri, validateMemory, validateTimeout } from '../validators'

describe('validateImageUri', () => {
  it('accepts a plain image tag', () => {
    expect(validateImageUri('ubuntu:24.04')).toBeNull()
  })

  it('accepts a registry path with digest', () => {
    expect(
      validateImageUri('registry.example.com/team/app@sha256:' + 'a'.repeat(64)),
    ).toBeNull()
  })

  it('rejects an empty value', () => {
    expect(validateImageUri('  ')).toBe('镜像地址不能为空')
  })

  it('rejects spaces in the name', () => {
    expect(validateImageUri('bad image')).toContain('格式无效')
  })
})

describe('validateCpu', () => {
  it('accepts millicore and plain numbers', () => {
    expect(validateCpu('500m')).toBeNull()
    expect(validateCpu('1.5')).toBeNull()
  })

  it('accepts empty as unset', () => {
    expect(validateCpu('')).toBeNull()
  })

  it('rejects garbage', () => {
    expect(validateCpu('abc')).toContain('格式无效')
  })
})

describe('validateMemory', () => {
  it('accepts Mi/Gi suffixes', () => {
    expect(validateMemory('512Mi')).toBeNull()
    expect(validateMemory('2Gi')).toBeNull()
  })

  it('rejects garbage', () => {
    expect(validateMemory('lots of ram')).toContain('格式无效')
  })
})

describe('validateTimeout', () => {
  it('accepts positive numbers and undefined', () => {
    expect(validateTimeout(300)).toBeNull()
    expect(validateTimeout(undefined)).toBeNull()
  })

  it('rejects zero and negatives', () => {
    expect(validateTimeout(0)).toContain('大于 0')
  })
})
```

`frontend/src/utils/__tests__/batchCreate.test.ts`：

```ts
import { describe, expect, it, vi } from 'vitest'
import { batchCreateSandboxes } from '../batchCreate'
import type { CreateSandboxRequest, CreateSandboxResponse } from '@/api/types'

const item = (index: number): CreateSandboxRequest => ({
  image: { uri: 'ubuntu:24.04' },
  entrypoint: ['/bin/sh'],
  resourceLimits: { cpu: '500m' },
  metadata: { index: String(index) },
})

const created = (id: string): CreateSandboxResponse => ({
  id,
  status: { state: 'Pending' },
  createdAt: '2026-01-01T00:00:00Z',
  entrypoint: ['/bin/sh'],
})

describe('batchCreateSandboxes', () => {
  it('creates all items and reports progress in order', async () => {
    const onProgress = vi.fn()
    const createFn = vi.fn((req: CreateSandboxRequest) =>
      Promise.resolve(created(`sb-${req.metadata!.index}`)),
    )

    const result = await batchCreateSandboxes([item(0), item(1)], {
      concurrency: 1,
      onProgress,
      createFn,
    })

    expect(result.successful).toHaveLength(2)
    expect(result.failed).toHaveLength(0)
    expect(onProgress).toHaveBeenNthCalledWith(1, 1, 2)
    expect(onProgress).toHaveBeenNthCalledWith(2, 2, 2)
  })

  it('splits successful and failed items', async () => {
    const createFn = vi.fn((req: CreateSandboxRequest) =>
      req.metadata!.index === '1'
        ? Promise.reject(new Error('image pull failed'))
        : Promise.resolve(created(`sb-${req.metadata!.index}`)),
    )

    const result = await batchCreateSandboxes([item(0), item(1), item(2)], {
      concurrency: 3,
      createFn,
    })

    expect(result.successful).toHaveLength(2)
    expect(result.failed).toHaveLength(1)
    expect(result.failed[0].error).toBe('image pull failed')
  })

  it('limits in-flight requests to concurrency', async () => {
    let active = 0
    const createFn = vi.fn(async (req: CreateSandboxRequest) => {
      active += 1
      expect(active).toBeLessThanOrEqual(2)
      await new Promise((resolve) => setTimeout(resolve, 10))
      active -= 1
      return created(`sb-${req.metadata!.index}`)
    })

    await batchCreateSandboxes([item(0), item(1), item(2), item(3)], {
      concurrency: 2,
      createFn,
    })

    expect(createFn).toHaveBeenCalledTimes(4)
  })
})
```

- [ ] **Step 6: 运行测试确认通过**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test`
Expected: 两个套件全部通过

Run: `npm run lint && npm run build`
Expected: 无错误

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat: add frontend API layer, hooks and utilities with tests

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 9: 布局、路由与入口 + 冒烟测试

**Files:**
- Create: `frontend/src/components/layout/AppLayout.tsx`
- Create: `frontend/src/pages/NotFound.tsx`
- Create: `frontend/src/router/index.tsx`
- Create: `frontend/src/components/layout/__tests__/AppLayout.test.tsx`
- Modify: `frontend/src/main.tsx`（整体替换）
- Modify: `frontend/src/App.tsx`（删除，入口由 main.tsx 直接挂 RouterProvider）

**Interfaces:**
- Consumes: Task 7 脚手架、Task 8 的 `@` alias 与 hooks
- Produces:
  - `AppLayout` — Sider 菜单（仪表盘/沙箱列表/创建沙箱/批量创建/设置）+ Header + `<Outlet/>`
  - `router`（createBrowserRouter）— 路由表，页面组件在 Task 10-15 逐个替换占位
  - 菜单 key 即路由路径：`/dashboard` `/sandboxes` `/create` `/batch-create` `/settings`

- [ ] **Step 1: 写布局**

`frontend/src/components/layout/AppLayout.tsx`：

```tsx
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Layout, Menu, Typography, theme } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  PlusOutlined,
  SettingOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons'

const { Sider, Header, Content } = Layout

const MENU_ITEMS = [
  { key: '/dashboard', icon: <AppstoreOutlined />, label: '仪表盘' },
  { key: '/sandboxes', icon: <BarsOutlined />, label: '沙箱列表' },
  { key: '/create', icon: <PlusOutlined />, label: '创建沙箱' },
  { key: '/batch-create', icon: <ThunderboltOutlined />, label: '批量创建' },
  { key: '/settings', icon: <SettingOutlined />, label: '设置' },
]

export default function AppLayout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { token } = theme.useToken()

  const selectedKey =
    MENU_ITEMS.find((item) => location.pathname.startsWith(item.key))?.key ?? '/dashboard'

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider width={220} theme="dark">
        <div
          style={{
            height: 56,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <Typography.Text strong style={{ color: '#fff', fontSize: 16 }}>
            OpenSandbox Admin
          </Typography.Text>
        </div>
        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[selectedKey]}
          items={MENU_ITEMS}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header style={{ background: token.colorBgContainer, padding: '0 24px' }}>
          <Typography.Title level={4} style={{ margin: '16px 0' }}>
            OpenSandbox 管理控制台
          </Typography.Title>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
```

- [ ] **Step 2: 写 NotFound 与路由**

`frontend/src/pages/NotFound.tsx`：

```tsx
import { useNavigate } from 'react-router-dom'
import { Button, Result } from 'antd'

export default function NotFound() {
  const navigate = useNavigate()
  return (
    <Result
      status="404"
      title="404"
      subTitle="页面不存在"
      extra={
        <Button type="primary" onClick={() => navigate('/dashboard')}>
          返回首页
        </Button>
      }
    />
  )
}
```

`frontend/src/router/index.tsx`：

```tsx
import { createBrowserRouter, Navigate } from 'react-router-dom'
import AppLayout from '@/components/layout/AppLayout'
import NotFound from '@/pages/NotFound'

// 占位组件：Task 10-15 逐个替换
const Placeholder = ({ title }: { title: string }) => (
  <div style={{ padding: 24 }}>{title}（待实现）</div>
)

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <Navigate to="/dashboard" replace /> },
      { path: 'dashboard', element: <Placeholder title="仪表盘" /> },
      { path: 'sandboxes', element: <Placeholder title="沙箱列表" /> },
      { path: 'sandboxes/:id', element: <Placeholder title="沙箱详情" /> },
      { path: 'create', element: <Placeholder title="创建沙箱" /> },
      { path: 'batch-create', element: <Placeholder title="批量创建" /> },
      { path: 'settings', element: <Placeholder title="设置" /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])
```

- [ ] **Step 3: 改写入口**

Modify `frontend/src/main.tsx` 整体替换：

```tsx
import React from 'react'
import ReactDOM from 'react-dom/client'
import { RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { router } from '@/router'
import 'antd/dist/reset.css'
import '@/styles/global.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 5000 } },
})

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider locale={zhCN}>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  </React.StrictMode>,
)
```

Run: `rm /e/GitHub/OpenSandBoxWeb/frontend/src/App.tsx`

- [ ] **Step 4: 写冒烟测试**

`frontend/src/components/layout/__tests__/AppLayout.test.tsx`：

```tsx
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import AppLayout from '../AppLayout'

describe('AppLayout', () => {
  it('renders all navigation items and page content', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/',
          element: <AppLayout />,
          children: [{ index: true, element: <div>页面内容</div> }],
        },
      ],
      { initialEntries: ['/'] },
    )

    render(
      <ConfigProvider>
        <AntApp>
          <RouterProvider router={router} />
        </AntApp>
      </ConfigProvider>,
    )

    expect(screen.getByText('OpenSandbox Admin')).toBeInTheDocument()
    expect(screen.getByText('仪表盘')).toBeInTheDocument()
    expect(screen.getByText('沙箱列表')).toBeInTheDocument()
    expect(screen.getByText('批量创建')).toBeInTheDocument()
    expect(screen.getByText('设置')).toBeInTheDocument()
    expect(screen.getByText('页面内容')).toBeInTheDocument()
  })
})
```

- [ ] **Step 5: 运行测试与构建**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 全部通过（新增 1 个套件）

Run: `npm run dev` → 打开 http://localhost:3000（后端 Task 6 代码已运行于 5080 时）验证：侧边菜单 5 项、点菜单跳转、URL 直达 `/settings` 刷新不 404（vite dev 自带 fallback）

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat: add app layout, router and entry with smoke test

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 10: Dashboard 页 + StatusBadge 组件

**Files:**
- Create: `frontend/src/components/common/StatusBadge.tsx`
- Create: `frontend/src/pages/Dashboard.tsx`
- Modify: `frontend/src/router/index.tsx`（dashboard 路由替换为真实组件）

**Interfaces:**
- Consumes: Task 8 的 usePolling/sandboxApi/formatTime/constants、Task 9 的路由
- Produces: `StatusBadge({state: string})`（Task 11/12 复用）

- [ ] **Step 1: 写 StatusBadge**

`frontend/src/components/common/StatusBadge.tsx`：

```tsx
import { Badge } from 'antd'

const COLORS: Record<string, string> = {
  Running: 'green',
  Paused: 'orange',
  Pending: 'blue',
  Pausing: 'cyan',
  Stopping: 'gold',
  Terminated: 'default',
  Failed: 'red',
}

export default function StatusBadge({ state }: { state: string }) {
  return <Badge color={COLORS[state] ?? 'default'} text={state} />
}
```

- [ ] **Step 2: 写 Dashboard**

`frontend/src/pages/Dashboard.tsx`：

```tsx
import { useNavigate } from 'react-router-dom'
import { Button, Card, Col, Row, Space, Statistic, Table, Typography } from 'antd'
import type { TableProps } from 'antd'
import { PlusOutlined, ThunderboltOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import type { Sandbox } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import { LIST_REFRESH_INTERVAL_MS } from '@/utils/constants'

const STAT_STATES = ['Running', 'Paused', 'Pending', 'Failed'] as const

const COLUMNS: TableProps<Sandbox>['columns'] = [
  { title: 'ID', dataIndex: 'id', key: 'id', ellipsis: true },
  { title: '镜像', dataIndex: ['image', 'uri'], key: 'image', ellipsis: true },
  {
    title: '状态',
    key: 'state',
    width: 120,
    render: (_, record) => <StatusBadge state={record.status.state} />,
  },
  {
    title: '创建时间',
    key: 'createdAt',
    width: 180,
    render: (_, record) => formatTime(record.createdAt),
  },
]

export default function Dashboard() {
  const navigate = useNavigate()
  const { data } = usePolling(
    ['sandboxes', 'dashboard'],
    () => sandboxApi.list({ page: 1, pageSize: 100 }),
    LIST_REFRESH_INTERVAL_MS,
  )

  const items = data?.items ?? []
  const count = (state: string) => items.filter((item) => item.status.state === state).length
  const recent = [...items].slice(0, 5)

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Row gutter={16}>
        {STAT_STATES.map((state) => (
          <Col key={state} xs={12} sm={12} md={6}>
            <Card>
              <Statistic title={state} value={count(state)} />
            </Card>
          </Col>
        ))}
      </Row>

      <Card
        title="快捷操作"
        extra={
          <Space>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/create')}>
              创建沙箱
            </Button>
            <Button icon={<ThunderboltOutlined />} onClick={() => navigate('/batch-create')}>
              批量创建
            </Button>
          </Space>
        }
      >
        <Typography.Text type="secondary">
          通过上方按钮快速创建沙箱，或前往沙箱列表管理已有实例。
        </Typography.Text>
      </Card>

      <Card title="最近创建">
        <Table rowKey="id" columns={COLUMNS} dataSource={recent} pagination={false} size="middle" />
      </Card>
    </Space>
  )
}
```

- [ ] **Step 3: 接入路由**

Modify `frontend/src/router/index.tsx`：加 `import Dashboard from '@/pages/Dashboard'`，把 `{ path: 'dashboard', element: <Placeholder title="仪表盘" /> }` 改为 `{ path: 'dashboard', element: <Dashboard /> }`。

- [ ] **Step 4: 验证并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 现有测试通过、lint/build 无错误

Run: `npm run dev` → http://localhost:3000/dashboard（后端运行且配置了可达的 OpenSandbox Domain 时）检查：4 个统计卡、快捷操作按钮跳转、最近创建表格

```bash
git add -A
git commit -m "feat: add dashboard page with status stat cards

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 11: SandboxList 页 + 测试

**Files:**
- Create: `frontend/src/pages/SandboxList.tsx`
- Create: `frontend/src/pages/__tests__/SandboxList.test.tsx`
- Modify: `frontend/src/router/index.tsx`（sandboxes 路由替换）

**Interfaces:**
- Consumes: Task 8 的 sandboxApi/usePolling/formatTime/constants、Task 10 的 StatusBadge
- Produces: 列表页（服务端分页 + 状态多选过滤 + 客户端关键词搜索 + 30s 轮询 + 批量删除）；刷新间隔读 `localStorage[STORAGE_KEY_REFRESH_INTERVAL]`（Task 15 Settings 写入）

- [ ] **Step 1: 写列表页**

`frontend/src/pages/SandboxList.tsx`：

```tsx
import { useState, type Key } from 'react'
import { useNavigate } from 'react-router-dom'
import { App, Button, Card, Input, Select, Space, Table, Typography } from 'antd'
import type { TableProps } from 'antd'
import { DeleteOutlined, ReloadOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { Sandbox } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import {
  DEFAULT_PAGE_SIZE,
  LIST_REFRESH_INTERVAL_MS,
  STORAGE_KEY_REFRESH_INTERVAL,
} from '@/utils/constants'

const STATE_OPTIONS = ['Running', 'Paused', 'Pending', 'Pausing', 'Stopping', 'Terminated', 'Failed']

export default function SandboxList() {
  const navigate = useNavigate()
  const { message, modal } = App.useApp()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [states, setStates] = useState<string[]>([])
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Key[]>([])

  const refreshInterval =
    Number(localStorage.getItem(STORAGE_KEY_REFRESH_INTERVAL)) || LIST_REFRESH_INTERVAL_MS

  const { data, isFetching, refetch } = usePolling(
    ['sandboxes', 'list', String(page), String(pageSize), states.join(',')],
    () => sandboxApi.list({ page, pageSize, state: states.length ? states : undefined }),
    refreshInterval,
  )

  // 关键词在服务端返回的当前页内过滤（ID + metadata 值）
  const filtered = (data?.items ?? []).filter((item) => {
    if (!search.trim()) return true
    const haystack = [item.id, ...Object.values(item.metadata ?? {})].join(' ')
    return haystack.toLowerCase().includes(search.trim().toLowerCase())
  })

  const confirmDelete = (ids: string[]) => {
    modal.confirm({
      title: '删除沙箱',
      content: `确定删除 ${ids.length} 个沙箱？该操作不可恢复。`,
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: async () => {
        try {
          const results = await Promise.allSettled(ids.map((id) => sandboxApi.remove(id)))
          const failedCount = results.filter((result) => result.status === 'rejected').length
          if (failedCount === 0) message.success(`已删除 ${ids.length} 个沙箱`)
          else message.warning(`删除完成，${failedCount} 个失败`)
          setSelected([])
          refetch()
        } catch (error) {
          message.error(extractErrorMessage(error))
        }
      },
    })
  }

  const columns: TableProps<Sandbox>['columns'] = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      ellipsis: true,
      render: (id: string) => (
        <Typography.Link onClick={() => navigate(`/sandboxes/${id}`)}>{id}</Typography.Link>
      ),
    },
    { title: '镜像', dataIndex: ['image', 'uri'], key: 'image', ellipsis: true },
    {
      title: '状态',
      key: 'state',
      width: 120,
      render: (_, record) => <StatusBadge state={record.status.state} />,
    },
    {
      title: '创建时间',
      key: 'createdAt',
      width: 180,
      render: (_, record) => formatTime(record.createdAt),
    },
    {
      title: '过期时间',
      key: 'expiresAt',
      width: 180,
      render: (_, record) => formatTime(record.expiresAt),
    },
    {
      title: '操作',
      key: 'actions',
      width: 150,
      render: (_, record) => (
        <Space>
          <Button size="small" onClick={() => navigate(`/sandboxes/${record.id}`)}>
            详情
          </Button>
          <Button
            size="small"
            danger
            icon={<DeleteOutlined />}
            onClick={() => confirmDelete([record.id])}
          />
        </Space>
      ),
    },
  ]

  return (
    <Card
      title="沙箱列表"
      extra={
        <Space>
          <Select
            mode="multiple"
            allowClear
            placeholder="按状态筛选"
            style={{ minWidth: 220 }}
            options={STATE_OPTIONS.map((state) => ({ value: state, label: state }))}
            value={states}
            onChange={setStates}
          />
          <Input.Search
            allowClear
            placeholder="搜索 ID 或元数据"
            style={{ width: 220 }}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <Button icon={<ReloadOutlined />} loading={isFetching} onClick={() => refetch()}>
            刷新
          </Button>
          <Button
            danger
            icon={<DeleteOutlined />}
            disabled={selected.length === 0}
            onClick={() => confirmDelete(selected.map(String))}
          >
            批量删除
          </Button>
        </Space>
      }
    >
      <Table
        rowKey="id"
        columns={columns}
        dataSource={filtered}
        loading={isFetching}
        rowSelection={{ selectedRowKeys: selected, onChange: setSelected }}
        pagination={{
          current: page,
          pageSize,
          total: data?.pagination.totalItems ?? 0,
          showSizeChanger: true,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPageSize !== pageSize ? 1 : nextPage)
            setPageSize(nextPageSize)
          },
        }}
      />
    </Card>
  )
}
```

- [ ] **Step 2: 写测试**

`frontend/src/pages/__tests__/SandboxList.test.tsx`：

```tsx
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import SandboxList from '../SandboxList'

vi.mock('@/api/sandbox', () => ({
  sandboxApi: {
    list: vi.fn().mockResolvedValue({
      items: [
        {
          id: 'sb-1',
          image: { uri: 'ubuntu:24.04' },
          status: { state: 'Running' },
          entrypoint: [],
          createdAt: '2026-01-01T00:00:00Z',
        },
        {
          id: 'sb-2',
          image: { uri: 'alpine:3.20' },
          status: { state: 'Paused' },
          entrypoint: [],
          createdAt: '2026-01-02T00:00:00Z',
        },
      ],
      pagination: { page: 1, pageSize: 20, totalItems: 2, totalPages: 1, hasNextPage: false },
    }),
    remove: vi.fn().mockResolvedValue(undefined),
  },
}))

describe('SandboxList', () => {
  it('renders rows returned by the API', async () => {
    const router = createMemoryRouter([{ path: '/', element: <SandboxList /> }], {
      initialEntries: ['/'],
    })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

    render(
      <ConfigProvider>
        <AntApp>
          <QueryClientProvider client={queryClient}>
            <RouterProvider router={router} />
          </QueryClientProvider>
        </AntApp>
      </ConfigProvider>,
    )

    expect(await screen.findByText('sb-1')).toBeInTheDocument()
    expect(screen.getByText('sb-2')).toBeInTheDocument()
    expect(screen.getByText('Running')).toBeInTheDocument()
  })
})
```

- [ ] **Step 3: 接入路由**

Modify `frontend/src/router/index.tsx`：加 `import SandboxList from '@/pages/SandboxList'`，把 `{ path: 'sandboxes', element: <Placeholder title="沙箱列表" /> }` 改为 `{ path: 'sandboxes', element: <SandboxList /> }`。

- [ ] **Step 4: 运行测试与构建**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 全部通过（新增 SandboxList 套件）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat: add sandbox list page with filtering and batch delete

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 12: SandboxDetail 页（指标流 + 图表 + TTL 续期）

**Files:**
- Create: `frontend/src/components/metrics/MetricsChart.tsx`
- Create: `frontend/src/pages/SandboxDetail.tsx`
- Modify: `frontend/src/router/index.tsx`（sandboxes/:id 路由替换）

**Interfaces:**
- Consumes: Task 8 的 useMetricsStream/usePolling/sandboxApi、Task 10 的 StatusBadge
- Produces: `MetricsChart({data: MetricsPoint[]})`（echarts 折线，双 Y 轴 CPU%/内存）

- [ ] **Step 1: 写 MetricsChart**

`frontend/src/components/metrics/MetricsChart.tsx`：

```tsx
import { useEffect, useRef } from 'react'
import * as echarts from 'echarts'
import type { MetricsPoint } from '@/api/types'

export default function MetricsChart({ data }: { data: MetricsPoint[] }) {
  const containerRef = useRef<HTMLDivElement>(null)
  const chartRef = useRef<echarts.ECharts | null>(null)

  useEffect(() => {
    if (!containerRef.current) return
    chartRef.current ??= echarts.init(containerRef.current)
    const chart = chartRef.current
    chart.setOption({
      tooltip: { trigger: 'axis' },
      legend: { data: ['CPU %', '内存 MiB'] },
      xAxis: { type: 'time' },
      yAxis: [
        { type: 'value', name: 'CPU %', max: 100 },
        { type: 'value', name: '内存 MiB' },
      ],
      series: [
        {
          name: 'CPU %',
          type: 'line',
          showSymbol: false,
          data: data.map((point) => [point.timestamp, point.cpu]),
        },
        {
          name: '内存 MiB',
          type: 'line',
          showSymbol: false,
          yAxisIndex: 1,
          data: data.map((point) => [point.timestamp, point.memory]),
        },
      ],
    })
  }, [data])

  useEffect(() => () => chartRef.current?.dispose(), [])

  return <div ref={containerRef} style={{ height: 320 }} />
}
```

- [ ] **Step 2: 写详情页**

`frontend/src/pages/SandboxDetail.tsx`：

```tsx
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  App,
  Button,
  Card,
  Col,
  DatePicker,
  Descriptions,
  Modal,
  Progress,
  Row,
  Space,
  Tag,
  Typography,
} from 'antd'
import {
  ClockCircleOutlined,
  DeleteOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
} from '@ant-design/icons'
import { useQueryClient } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { MetricsPoint } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import MetricsChart from '@/components/metrics/MetricsChart'
import { useMetricsStream } from '@/hooks/useMetricsStream'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import { DETAIL_REFRESH_INTERVAL_MS, METRICS_HISTORY_LIMIT } from '@/utils/constants'

export default function SandboxDetail() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [renewOpen, setRenewOpen] = useState(false)
  const [renewAt, setRenewAt] = useState<dayjs.Dayjs | null>(null)
  const [renewing, setRenewing] = useState(false)
  const [history, setHistory] = useState<MetricsPoint[]>([])

  const { data: sandbox, isFetching } = usePolling(
    ['sandbox', id],
    () => sandboxApi.get(id),
    DETAIL_REFRESH_INTERVAL_MS,
    Boolean(id),
  )

  const isRunning = sandbox?.status.state === 'Running'

  useMetricsStream(
    id,
    (event) => {
      if (event.type === 'metrics' && event.data) {
        const point: MetricsPoint = {
          timestamp: event.data.timestamp,
          cpu: event.data.cpuUsedPct,
          memory: event.data.memUsedMib,
        }
        setHistory((prev) => [...prev.slice(-(METRICS_HISTORY_LIMIT - 1)), point])
      }
    },
    isRunning,
  )

  const latest = history[history.length - 1]
  const memoryPct =
    latest && latest.memTotalMib > 0
      ? Math.min(100, Math.round((latest.memUsedMib / latest.memTotalMib) * 100))
      : 0

  const runAction = async (action: 'pause' | 'resume' | 'delete', label: string) => {
    try {
      if (action === 'delete') await sandboxApi.remove(id)
      if (action === 'pause') await sandboxApi.pause(id)
      if (action === 'resume') await sandboxApi.resume(id)
      message.success(`已${label}`)
      queryClient.invalidateQueries({ queryKey: ['sandbox', id] })
      if (action === 'delete') navigate('/sandboxes')
    } catch (error) {
      message.error(extractErrorMessage(error))
    }
  }

  const confirmDelete = () => {
    modal.confirm({
      title: '删除沙箱',
      content: `确定删除沙箱 ${id}？该操作不可恢复。`,
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => runAction('delete', '删除'),
    })
  }

  const submitRenew = async () => {
    if (!renewAt) return
    setRenewing(true)
    try {
      await sandboxApi.renewExpiration(id, { expiresAt: renewAt.toISOString() })
      message.success('过期时间已更新')
      setRenewOpen(false)
      queryClient.invalidateQueries({ queryKey: ['sandbox', id] })
    } catch (error) {
      message.error(extractErrorMessage(error))
    } finally {
      setRenewing(false)
    }
  }

  if (!sandbox) return isFetching ? <Card loading /> : null

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Card
        title={
          <Space>
            <Typography.Text strong>{sandbox.id}</Typography.Text>
            <StatusBadge state={sandbox.status.state} />
          </Space>
        }
        extra={
          <Space>
            {isRunning ? (
              <Button icon={<PauseCircleOutlined />} onClick={() => runAction('pause', '暂停')}>
                暂停
              </Button>
            ) : (
              <Button icon={<PlayCircleOutlined />} onClick={() => runAction('resume', '恢复')}>
                恢复
              </Button>
            )}
            <Button icon={<ClockCircleOutlined />} onClick={() => setRenewOpen(true)}>
              续期
            </Button>
            <Button danger icon={<DeleteOutlined />} onClick={confirmDelete}>
              删除
            </Button>
          </Space>
        }
      >
        <Descriptions column={2} bordered size="small">
          <Descriptions.Item label="镜像">{sandbox.image.uri}</Descriptions.Item>
          <Descriptions.Item label="入口命令">
            {sandbox.entrypoint.join(' ') || '-'}
          </Descriptions.Item>
          <Descriptions.Item label="创建时间">{formatTime(sandbox.createdAt)}</Descriptions.Item>
          <Descriptions.Item label="过期时间">{formatTime(sandbox.expiresAt)}</Descriptions.Item>
          <Descriptions.Item label="状态说明">
            {sandbox.status.message || sandbox.status.reason || '-'}
          </Descriptions.Item>
          <Descriptions.Item label="元数据">
            {Object.entries(sandbox.metadata ?? {}).map(([key, value]) => (
              <Tag key={key}>{`${key}=${value}`}</Tag>
            ))}
          </Descriptions.Item>
        </Descriptions>
      </Card>

      {isRunning && (
        <Card title="实时监控">
          <Row gutter={16} style={{ marginBottom: 16 }}>
            <Col span={8}>
              <Progress
                type="dashboard"
                percent={latest ? Math.min(100, Math.round(latest.cpu)) : 0}
                format={(percent) => `CPU ${percent}%`}
              />
            </Col>
            <Col span={8}>
              <Progress type="dashboard" percent={memoryPct} format={(percent) => `内存 ${percent}%`} />
            </Col>
            <Col span={8}>
              <Descriptions column={1} size="small" bordered>
                <Descriptions.Item label="CPU 核数">{latest?.cpuCount ?? '-'}</Descriptions.Item>
                <Descriptions.Item label="内存用量">
                  {latest
                    ? `${latest.memUsedMib.toFixed(0)} / ${latest.memTotalMib.toFixed(0)} MiB`
                    : '-'}
                </Descriptions.Item>
              </Descriptions>
            </Col>
          </Row>
          <MetricsChart data={history} />
        </Card>
      )}

      <Modal
        title="延长过期时间"
        open={renewOpen}
        onOk={submitRenew}
        confirmLoading={renewing}
        onCancel={() => setRenewOpen(false)}
        okText="确定"
        cancelText="取消"
      >
        <DatePicker
          showTime
          style={{ width: '100%' }}
          value={renewAt}
          onChange={setRenewAt}
          placeholder="选择新的过期时间"
        />
      </Modal>
    </Space>
  )
}
```

- [ ] **Step 3: 接入路由**

Modify `frontend/src/router/index.tsx`：加 `import SandboxDetail from '@/pages/SandboxDetail'`，把 `{ path: 'sandboxes/:id', element: <Placeholder title="沙箱详情" /> }` 改为 `{ path: 'sandboxes/:id', element: <SandboxDetail /> }`。

- [ ] **Step 4: 验证（无自动化测试；图表 + SSE 在 jsdom 下成本高、收益低）**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 现有测试通过、lint/build 无错误

Run: `npm run dev` → 列表点进某个 Running 沙箱（需后端配置可达平台）检查：配置 Descriptions、CPU/内存仪表、echarts 折线随 SSE 每 2s 追加、暂停/恢复/续期/删除按钮行为；curl 确认后端 SSE：`curl -N http://localhost:5080/api/v1/sandboxes/<id>/metrics/watch`

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat: add sandbox detail page with live metrics and TTL renewal

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 13: CreateSandbox 页

**Files:**
- Create: `frontend/src/pages/CreateSandbox.tsx`
- Modify: `frontend/src/router/index.tsx`（create 路由替换）

**Interfaces:**
- Consumes: Task 8 的 sandboxApi/validators/extractErrorMessage、Task 9 的路由
- Produces: 创建页（镜像/名称/入口命令/资源配额/超时/认证/环境变量/网络策略/卷）

- [ ] **Step 1: 写创建页**

`frontend/src/pages/CreateSandbox.tsx`：

```tsx
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  App,
  Button,
  Card,
  Col,
  Form,
  Input,
  InputNumber,
  Radio,
  Row,
  Select,
  Space,
} from 'antd'
import { MinusCircleOutlined, PlusOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { CreateSandboxRequest, NetworkRule, VolumeSpec } from '@/api/types'
import { validateCpu, validateImageUri, validateMemory, validateTimeout } from '@/utils/validators'

interface CreateFormValues {
  imageUri: string
  imageUser?: string
  imagePassword?: string
  name?: string
  entrypoint?: string[]
  cpu?: string
  memory?: string
  gpu?: string
  timeout?: number
  env?: { key: string; value: string }[]
  defaultAction?: 'allow' | 'deny'
  egress?: { action: 'allow' | 'deny'; target: string }[]
  volumes?: { name: string; mountPath: string; hostPath?: string }[]
}

export default function CreateSandbox() {
  const navigate = useNavigate()
  const { message } = App.useApp()
  const [form] = Form.useForm<CreateFormValues>()
  const [submitting, setSubmitting] = useState(false)

  const onFinish = async (values: CreateFormValues) => {
    const resourceLimits = Object.fromEntries(
      Object.entries({ cpu: values.cpu ?? '', memory: values.memory ?? '', gpu: values.gpu ?? '' })
        .filter(([, value]) => value.trim()),
    )
    const env = (values.env ?? [])
      .filter((kv) => kv.key.trim())
      .reduce<Record<string, string>>((acc, kv) => {
        acc[kv.key.trim()] = kv.value ?? ''
        return acc
      }, {})
    const request: CreateSandboxRequest = {
      image: {
        uri: values.imageUri.trim(),
        ...(values.imageUser || values.imagePassword
          ? { auth: { username: values.imageUser ?? '', password: values.imagePassword ?? '' } }
          : {}),
      },
      entrypoint: values.entrypoint ?? [],
      resourceLimits,
      timeout: values.timeout ?? null,
      env: Object.keys(env).length ? env : undefined,
      metadata: values.name ? { name: values.name } : undefined,
      networkPolicy:
        values.defaultAction || (values.egress ?? []).length
          ? {
              defaultAction: values.defaultAction,
              egress: (values.egress ?? [])
                .filter((rule) => rule.target.trim())
                .map<NetworkRule>((rule) => ({
                  action: rule.action,
                  target: rule.target.trim(),
                })),
            }
          : undefined,
      volumes: (values.volumes ?? [])
        .filter((v) => v.name.trim() && v.mountPath.trim())
        .map<VolumeSpec>((v) => ({
          name: v.name.trim(),
          mountPath: v.mountPath.trim(),
          hostPath: v.hostPath?.trim() || undefined,
        })),
    }

    setSubmitting(true)
    try {
      const created = await sandboxApi.create(request)
      message.success(`沙箱 ${created.id} 创建成功`)
      navigate(`/sandboxes/${created.id}`)
    } catch (error) {
      message.error(extractErrorMessage(error))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card title="创建沙箱" style={{ maxWidth: 900 }}>
      <Form
        form={form}
        layout="vertical"
        onFinish={onFinish}
        initialValues={{ entrypoint: [], env: [], egress: [], volumes: [], defaultAction: 'allow' }}
      >
        <Row gutter={16}>
          <Col span={12}>
            <Form.Item
              name="imageUri"
              label="镜像地址"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateImageUri(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 ubuntu:24.04 或 registry.example.com/repo/img:tag" />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="name" label="名称（写入 metadata.name）">
              <Input placeholder="可选" />
            </Form.Item>
          </Col>
        </Row>

        <Form.Item name="entrypoint" label="入口命令（可多个，按顺序执行）">
          <Select mode="tags" placeholder="如 /bin/sh" open={false} />
        </Form.Item>

        <Row gutter={16}>
          <Col span={8}>
            <Form.Item
              name="cpu"
              label="CPU 配额"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateCpu(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 500m" />
            </Form.Item>
          </Col>
          <Col span={8}>
            <Form.Item
              name="memory"
              label="内存配额"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateMemory(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 512Mi" />
            </Form.Item>
          </Col>
          <Col span={8}>
            <Form.Item name="gpu" label="GPU 数量">
              <Input placeholder="如 1" />
            </Form.Item>
          </Col>
        </Row>

        <Form.Item
          name="timeout"
          label="超时时间（秒）"
          rules={[
            {
              validator: (_, value: number | undefined) => {
                const error = validateTimeout(value)
                return error ? Promise.reject(new Error(error)) : Promise.resolve()
              },
            },
          ]}
        >
          <InputNumber min={1} style={{ width: 200 }} placeholder="可选" />
        </Form.Item>

        <Form.Item label="镜像仓库认证">
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item name="imageUser" noStyle>
                <Input placeholder="用户名（可选）" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item name="imagePassword" noStyle>
                <Input.Password placeholder="密码（可选）" />
              </Form.Item>
            </Col>
          </Row>
        </Form.Item>

        <Form.List name="env">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'key']}>
                    <Input placeholder="环境变量名" />
                  </Form.Item>
                  <Form.Item name={[name, 'value']}>
                    <Input placeholder="值" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add()}>
                添加环境变量
              </Button>
            </>
          )}
        </Form.List>

        <Form.Item name="defaultAction" label="网络策略默认动作">
          <Radio.Group>
            <Radio value="allow">允许</Radio>
            <Radio value="deny">拒绝</Radio>
          </Radio.Group>
        </Form.Item>

        <Form.List name="egress">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'action']}>
                    <Select
                      style={{ width: 120 }}
                      options={[
                        { value: 'allow', label: 'allow' },
                        { value: 'deny', label: 'deny' },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item name={[name, 'target']}>
                    <Input placeholder="目标 CIDR，如 10.0.0.0/8" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({ action: 'allow' })}>
                添加出站规则
              </Button>
            </>
          )}
        </Form.List>

        <Form.List name="volumes">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'name']}>
                    <Input placeholder="卷名" />
                  </Form.Item>
                  <Form.Item name={[name, 'mountPath']}>
                    <Input placeholder="挂载路径，如 /data" />
                  </Form.Item>
                  <Form.Item name={[name, 'hostPath']}>
                    <Input placeholder="主机路径（可选）" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add()}>
                添加卷
              </Button>
            </>
          )}
        </Form.List>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={submitting}>
            创建沙箱
          </Button>
        </Form.Item>
      </Form>
    </Card>
  )
}
```

- [ ] **Step 2: 接入路由**

Modify `frontend/src/router/index.tsx`：加 `import CreateSandbox from '@/pages/CreateSandbox'`，把 `{ path: 'create', element: <Placeholder title="创建沙箱" /> }` 改为 `{ path: 'create', element: <CreateSandbox /> }`。

- [ ] **Step 3: 验证并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 现有测试通过、lint/build 无错误

Run: `npm run dev` → /create 检查：镜像格式校验报错、必填校验、提交成功后跳详情页

```bash
git add -A
git commit -m "feat: add create sandbox page with full option form

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 14: BatchCreate 页

**Files:**
- Create: `frontend/src/pages/BatchCreate.tsx`
- Modify: `frontend/src/router/index.tsx`（batch-create 路由替换）

**Interfaces:**
- Consumes: Task 8 的 batchCreateSandboxes/validateImageUri
- Produces: 批量创建页（JSON 导入 + 手动编辑表格 + 进度 + 结果汇总）；批量逻辑单测已在 Task 8 覆盖

- [ ] **Step 1: 写批量创建页**

`frontend/src/pages/BatchCreate.tsx`：

```tsx
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { App, Button, Card, Input, List, Progress, Space, Table, Tabs, Typography } from 'antd'
import type { TableProps } from 'antd'
import { InboxOutlined, PlusOutlined } from '@ant-design/icons'
import { batchCreateSandboxes } from '@/utils/batchCreate'
import type { CreateSandboxRequest } from '@/api/types'
import { validateImageUri } from '@/utils/validators'

interface BatchCreateItem {
  id: string
  image: string
  name?: string
  timeout?: number
  cpu?: string
  memory?: string
  entrypoint?: string[]
  env?: Record<string, string>
  metadata?: Record<string, string>
}

interface BatchResultView {
  successful: { image: string; id: string }[]
  failed: { image: string; error: string }[]
}

const toRequest = (item: BatchCreateItem): CreateSandboxRequest => ({
  image: { uri: item.image },
  entrypoint: item.entrypoint ?? [],
  resourceLimits: Object.fromEntries(
    Object.entries({ cpu: item.cpu ?? '', memory: item.memory ?? '' }).filter(([, v]) => v.trim()),
  ),
  timeout: item.timeout ?? null,
  env: item.env,
  metadata: item.metadata ?? (item.name ? { name: item.name } : undefined),
})

function parseJsonItems(text: string): BatchCreateItem[] | null {
  if (!text.trim()) return null
  try {
    const parsed: unknown = JSON.parse(text)
    if (!Array.isArray(parsed)) return null
    return parsed.map((raw, index) => {
      const entry = (raw ?? {}) as Record<string, unknown>
      return {
        id: `${Date.now()}-${index}`,
        image: String(entry.image ?? ''),
        name: entry.name != null ? String(entry.name) : undefined,
        timeout: typeof entry.timeout === 'number' ? entry.timeout : undefined,
        cpu: entry.cpu != null ? String(entry.cpu) : undefined,
        memory: entry.memory != null ? String(entry.memory) : undefined,
        entrypoint: Array.isArray(entry.entrypoint) ? entry.entrypoint.map(String) : undefined,
        env:
          entry.env && typeof entry.env === 'object'
            ? (entry.env as Record<string, string>)
            : undefined,
        metadata:
          entry.metadata && typeof entry.metadata === 'object'
            ? (entry.metadata as Record<string, string>)
            : undefined,
      }
    })
  } catch {
    return null
  }
}

export default function BatchCreate() {
  const navigate = useNavigate()
  const { message } = App.useApp()
  const [jsonText, setJsonText] = useState('')
  const [items, setItems] = useState<BatchCreateItem[]>([])
  const [progress, setProgress] = useState<{ completed: number; total: number } | null>(null)
  const [result, setResult] = useState<BatchResultView | null>(null)

  const invalidItems = useMemo(
    () => items.filter((item) => validateImageUri(item.image) !== null),
    [items],
  )

  const importJson = () => {
    const parsed = parseJsonItems(jsonText)
    if (parsed === null) {
      message.error('JSON 格式无效：需要包含沙箱对象数组')
      return
    }
    setItems(parsed)
    setResult(null)
    message.success(`已导入 ${parsed.length} 条配置`)
  }

  const addRow = () => {
    setItems((prev) => [...prev, { id: `manual-${prev.length}-${Date.now()}`, image: '' }])
    setResult(null)
  }

  const updateItem = (id: string, patch: Partial<BatchCreateItem>) => {
    setItems((prev) => prev.map((item) => (item.id === id ? { ...item, ...patch } : item)))
  }

  const removeItem = (id: string) => {
    setItems((prev) => prev.filter((item) => item.id !== id))
  }

  const startBatch = async () => {
    if (items.length === 0) {
      message.warning('请先导入或添加沙箱配置')
      return
    }
    if (invalidItems.length > 0) {
      message.error(`${invalidItems.length} 条配置镜像地址无效`)
      return
    }
    setProgress({ completed: 0, total: items.length })
    setResult(null)
    const outcome = await batchCreateSandboxes(items.map(toRequest), {
      onProgress: (completed, total) => setProgress({ completed, total }),
    })
    setResult({
      successful: outcome.successful.map((entry) => ({
        image: entry.request.image.uri,
        id: entry.id,
      })),
      failed: outcome.failed.map((entry) => ({
        image: entry.request.image.uri,
        error: entry.error,
      })),
    })
    setProgress(null)
    message.success(`批量创建完成：成功 ${outcome.successful.length}，失败 ${outcome.failed.length}`)
  }

  const columns: TableProps<BatchCreateItem>['columns'] = [
    {
      title: '镜像',
      dataIndex: 'image',
      key: 'image',
      render: (image: string, record) => (
        <Input
          value={image}
          status={validateImageUri(image) ? 'error' : ''}
          onChange={(event) => updateItem(record.id, { image: event.target.value })}
        />
      ),
    },
    {
      title: '名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string | undefined, record) => (
        <Input
          value={name}
          onChange={(event) => updateItem(record.id, { name: event.target.value || undefined })}
        />
      ),
    },
    {
      title: 'CPU',
      dataIndex: 'cpu',
      key: 'cpu',
      width: 110,
      render: (cpu: string | undefined, record) => (
        <Input
          value={cpu}
          placeholder="500m"
          onChange={(event) => updateItem(record.id, { cpu: event.target.value || undefined })}
        />
      ),
    },
    {
      title: '内存',
      dataIndex: 'memory',
      key: 'memory',
      width: 110,
      render: (memory: string | undefined, record) => (
        <Input
          value={memory}
          placeholder="512Mi"
          onChange={(event) => updateItem(record.id, { memory: event.target.value || undefined })}
        />
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_, record) => (
        <Button danger size="small" onClick={() => removeItem(record.id)}>
          移除
        </Button>
      ),
    },
  ]

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Card title="批量创建沙箱">
        <Tabs
          items={[
            {
              key: 'json',
              label: 'JSON 导入',
              children: (
                <Space direction="vertical" style={{ width: '100%' }}>
                  <Input.TextArea
                    rows={10}
                    value={jsonText}
                    onChange={(event) => setJsonText(event.target.value)}
                    placeholder={'[\n  { "image": "ubuntu:24.04", "name": "demo", "cpu": "500m", "memory": "512Mi", "entrypoint": ["/bin/sh"] }\n]'}
                  />
                  <Button type="primary" icon={<InboxOutlined />} onClick={importJson}>
                    导入
                  </Button>
                </Space>
              ),
            },
            {
              key: 'manual',
              label: '手动添加',
              children: (
                <Button type="dashed" icon={<PlusOutlined />} onClick={addRow}>
                  添加一行
                </Button>
              ),
            },
          ]}
        />
      </Card>

      <Card
        title={`配置预览（${items.length} 条，${invalidItems.length} 条镜像无效）`}
        extra={
          <Space>
            {progress && (
              <Progress
                percent={Math.round((progress.completed / progress.total) * 100)}
                style={{ width: 200 }}
                status="active"
              />
            )}
            <Button
              type="primary"
              onClick={startBatch}
              disabled={items.length === 0 || invalidItems.length > 0}
            >
              开始创建
            </Button>
          </Space>
        }
      >
        <Table rowKey="id" columns={columns} dataSource={items} pagination={false} size="small" />
      </Card>

      {result && (
        <Card title="创建结果">
          <Typography.Paragraph>
            成功 {result.successful.length} 个，失败 {result.failed.length} 个
          </Typography.Paragraph>
          <List
            size="small"
            dataSource={result.successful}
            renderItem={(entry) => (
              <List.Item>
                <Space>
                  <Typography.Text>{entry.image}</Typography.Text>
                  <Typography.Link onClick={() => navigate(`/sandboxes/${entry.id}`)}>
                    {entry.id}
                  </Typography.Link>
                </Space>
              </List.Item>
            )}
          />
          {result.failed.length > 0 && (
            <List
              size="small"
              header={<Typography.Text type="danger">失败项</Typography.Text>}
              dataSource={result.failed}
              renderItem={(entry) => (
                <List.Item>
                  <Space>
                    <Typography.Text>{entry.image}</Typography.Text>
                    <Typography.Text type="danger">{entry.error}</Typography.Text>
                  </Space>
                </List.Item>
              )}
            />
          )}
        </Card>
      )}
    </Space>
  )
}
```

- [ ] **Step 2: 接入路由**

Modify `frontend/src/router/index.tsx`：加 `import BatchCreate from '@/pages/BatchCreate'`，把 `{ path: 'batch-create', element: <Placeholder title="批量创建" /> }` 改为 `{ path: 'batch-create', element: <BatchCreate /> }`。

- [ ] **Step 3: 验证并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 现有测试（含 Task 8 批量逻辑单测）通过、lint/build 无错误

Run: `npm run dev` → /batch-create 检查：JSON 导入解析、无效镜像行标红并禁用创建、进度条推进、结果汇总

```bash
git add -A
git commit -m "feat: add batch create page with JSON import and progress

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 15: Settings 页

**Files:**
- Create: `frontend/src/pages/Settings.tsx`
- Modify: `frontend/src/router/index.tsx`（settings 路由替换）

**Interfaces:**
- Consumes: Task 8 的 STORAGE_KEY_REFRESH_INTERVAL（Task 11 列表页读取）
- Produces: 设置页（刷新间隔持久化、API 端点信息、关于）

> 设计变更说明：旧前端在浏览器本地保存 API Key 直连平台；新架构中 API Key 由后端配置管理（appsettings `OpenSandbox:ApiKey`），Settings 不再提供 API Key 输入框。

- [ ] **Step 1: 写设置页**

`frontend/src/pages/Settings.tsx`：

```tsx
import { useState } from 'react'
import { App, Card, Descriptions, Divider, Select, Typography } from 'antd'
import { STORAGE_KEY_REFRESH_INTERVAL } from '@/utils/constants'

const INTERVAL_OPTIONS = [
  { value: 10000, label: '10 秒' },
  { value: 30000, label: '30 秒' },
  { value: 60000, label: '60 秒' },
]

export default function Settings() {
  const { message } = App.useApp()
  const [interval, setIntervalValue] = useState(
    () => Number(localStorage.getItem(STORAGE_KEY_REFRESH_INTERVAL)) || 30000,
  )

  const changeInterval = (value: number) => {
    setIntervalValue(value)
    localStorage.setItem(STORAGE_KEY_REFRESH_INTERVAL, String(value))
    message.success('设置已保存')
  }

  return (
    <Card title="设置" style={{ maxWidth: 640 }}>
      <Typography.Title level={5}>刷新间隔</Typography.Title>
      <Typography.Paragraph type="secondary">
        沙箱列表自动刷新间隔（保存后对后续打开生效）。
      </Typography.Paragraph>
      <Select style={{ width: 200 }} value={interval} options={INTERVAL_OPTIONS} onChange={changeInterval} />

      <Divider />

      <Typography.Title level={5}>API 端点配置</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="管理 API">/api/v1（由后端服务托管）</Descriptions.Item>
        <Descriptions.Item label="指标流">
          /api/v1/sandboxes/:id/metrics/watch（SSE）
        </Descriptions.Item>
      </Descriptions>

      <Divider />

      <Typography.Title level={5}>关于</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="版本">1.0.0</Descriptions.Item>
        <Descriptions.Item label="项目地址">
          <Typography.Link href="https://github.com/alibaba/OpenSandbox" target="_blank">
            https://github.com/alibaba/OpenSandbox
          </Typography.Link>
        </Descriptions.Item>
      </Descriptions>
    </Card>
  )
}
```

- [ ] **Step 2: 接入路由并清理占位**

Modify `frontend/src/router/index.tsx`：加 `import Settings from '@/pages/Settings'`，把 `{ path: 'settings', element: <Placeholder title="设置" /> }` 改为 `{ path: 'settings', element: <Settings /> }`，并删除 `Placeholder` 组件定义（已无引用）。

- [ ] **Step 3: 验证并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm test && npm run lint && npm run build`
Expected: 全部通过

Run: `npm run dev` → /settings 检查：改刷新间隔后刷新列表页间隔变化（React Query refetchInterval 重新挂载生效）、localStorage 持久化

```bash
git add -A
git commit -m "feat: add settings page with refresh interval persistence

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 16: SPA 静态托管接入

**Files:**
- Create: `backend/Host/Web/SpaFallbackEndpoints.cs`
- Modify: `backend/Program.cs`（挂 MapSpaFallback）

**Interfaces:**
- Consumes: Task 2 的 csproj Content include（frontend/dist → wwwroot）
- Produces: 生产/联调单进程托管（根路径与未知路径返回 index.html，/api 不受影响）

- [ ] **Step 1: 写 SPA fallback**

`backend/Host/Web/SpaFallbackEndpoints.cs`：

```csharp
namespace OpenSandbox.Web.Host.Web;

public static class SpaFallbackEndpoints
{
    public static WebApplication MapSpaFallback(this WebApplication app)
    {
        // dev（dotnet run）时前端产物随 csproj Content 复制到 bin 输出目录；
        // 生产发布时在 ContentRoot/wwwroot。仅当 index.html 存在时才启用托管，
        // 纯 API 模式（无前端构建）下不注册 fallback。
        if (app.Environment.IsDevelopment())
            app.Environment.WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

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
```

- [ ] **Step 2: 挂载**

Modify `backend/Program.cs`：在 `app.MapMetricsEndpoints();` 后加一行（必须在 `app.Run()` 前）：

```csharp
app.MapSpaFallback();
```

- [ ] **Step 3: 构建前端并验证**

Run: `cd /e/GitHub/OpenSandBoxWeb/frontend && npm run build && cd ../backend && dotnet run`

Run（另开终端）:
- `curl -s http://localhost:5080/ | head -c 200` → 返回 HTML（index.html）
- `curl -s http://localhost:5080/sandboxes | head -c 200` → 同样返回 index.html（SPA fallback）
- `curl -s http://localhost:5080/api/v1/health` → `{"status":"ok",...}`（API 不受影响）
- 浏览器打开 http://localhost:5080/ → 完整 UI 可交互（API 同源直连）

- [ ] **Step 4: 回归后端测试并提交**

Run: `cd /e/GitHub/OpenSandBoxWeb && dotnet test tests/OpenSandbox.Web.Tests`
Expected: 全部通过（fallback 在测试环境因 wwwroot 缺失自动跳过）

```bash
git add -A
git commit -m "feat: serve frontend build with SPA fallback from backend

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 17: Docker 部署文件

**Files:**
- Create: `Dockerfile`
- Create: `docker-compose.yml`
- Create: `.dockerignore`
- Create: `.env.example`

**Interfaces:**
- Consumes: Task 16 的静态托管
- Produces: `docker compose up` 一键运行（5080）

- [ ] **Step 1: 写 Dockerfile**

`Dockerfile`：

```dockerfile
FROM node:24-alpine AS frontend
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/ ./backend/
COPY --from=frontend /src/dist ./frontend/dist
RUN dotnet publish backend/OpenSandbox.Web.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend /app/out ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "OpenSandbox.Web.dll"]
```

- [ ] **Step 2: 写 compose 与 ignore**

`docker-compose.yml`：

```yaml
services:
  opensandbox-web:
    build: .
    ports:
      - "5080:5080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      OpenSandbox__Domain: ${OPENSANDBOX_DOMAIN:-}
      OpenSandbox__ApiKey: ${OPENSANDBOX_API_KEY:-}
    restart: unless-stopped
```

`.env.example`：

```env
# OpenSandbox 平台地址与密钥（docker compose 注入后端配置）
OPENSANDBOX_DOMAIN=https://sandbox.example.com
OPENSANDBOX_API_KEY=your-api-key
```

`.dockerignore`：

```gitignore
.git
**/node_modules
frontend/dist
backend/bin
backend/obj
tests
docs
screenshots
.env
```

- [ ] **Step 3: 验证**

Run: `cd /e/GitHub/OpenSandBoxWeb && docker compose config`
Expected: 配置合法（services.opensandbox-web 定义完整）

Run: `docker build -t opensandbox-web .`
Expected: 构建成功。若本机无 Docker，记录跳过并标注"待有 Docker 环境时验证"。

Run: `cp .env.example .env && docker compose up -d && curl http://localhost:5080/api/v1/health && docker compose down`
Expected: health 返回 ok

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "feat: add multi-stage Dockerfile and docker compose

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

### Task 18: README 双语重写

**Files:**
- Modify: `README.md`（整体替换）
- Modify: `screenshots/`（手动更新，见 Step 4）

**Interfaces:**
- Consumes: 全部已实现功能
- Produces: 面向用户的双语文档

- [ ] **Step 1: 写 README（完整内容如下，整体替换）**

```markdown
# OpenSandbox Admin

[English](#english) | [中文](#中文)

---

<a name="english"></a>
## English

### Overview

OpenSandbox Admin is a modern web-based management console for **OpenSandbox** - a lightweight, secure sandbox environment orchestration platform. This admin dashboard provides a comprehensive interface for managing containerized sandbox instances, monitoring resource usage, and controlling sandbox lifecycles.

The repository follows a frontend/backend split: a React SPA and a .NET 10 API that wraps the official [Alibaba.OpenSandbox](https://www.nuget.org/packages/Alibaba.OpenSandbox) SDK.

### Features

- **Dashboard Overview** - Real-time statistics of sandbox states (Running, Paused, Pending, Failed)
- **Sandbox Management** - Create single or batch sandboxes from container images; pause, resume and delete instances; filter and search by state or metadata
- **Real-time Metrics** - Live CPU and memory monitoring streamed over SSE through the backend proxy
- **TTL Management** - Extend sandbox expiration with a datetime picker
- **Batch Operations** - Create multiple sandboxes via JSON import or manual input with progress tracking
- **Single-process Deployment** - Backend serves the built frontend with SPA fallback

### Tech Stack

| Area | Technology |
|------|------------|
| Frontend | React 19, TypeScript, Vite, Ant Design 5, TanStack Query, ECharts, axios, dayjs |
| Backend | .NET 10, ASP.NET Core Minimal APIs, Alibaba.OpenSandbox 1.1.1 |
| Tests | xUnit + WebApplicationFactory (backend), Vitest + Testing Library (frontend) |

### Repository Structure

```
├── backend/       # .NET 10 API (Contracts / Host / Infrastructure layers)
├── frontend/      # React 19 + Vite + Ant Design SPA
├── tests/         # xUnit test project
├── Dockerfile     # Multi-stage: node build -> dotnet publish -> runtime
└── docker-compose.yml
```

### Getting Started

Prerequisites: .NET SDK 10+, Node.js 20+

```bash
# Terminal 1 - backend (http://localhost:5080)
cd backend
dotnet run

# Terminal 2 - frontend dev server (http://localhost:3000, proxies /api to 5080)
cd frontend
npm install
npm run dev
```

### Configuration

Backend settings in `backend/appsettings.json`:

```json
{
  "OpenSandbox": {
    "Domain": "https://sandbox.example.com",
    "ApiKey": "your-api-key"
  }
}
```

### API

All endpoints are prefixed with `/api/v1`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Liveness check |
| `/sandboxes` | GET | List sandboxes (pagination, state/metadata filters) |
| `/sandboxes` | POST | Create a sandbox |
| `/sandboxes/{id}` | GET | Sandbox details |
| `/sandboxes/{id}` | DELETE | Delete a sandbox |
| `/sandboxes/{id}/pause` | POST | Pause a sandbox |
| `/sandboxes/{id}/resume` | POST | Resume a sandbox |
| `/sandboxes/{id}/renew-expiration` | POST | Extend expiration time |
| `/sandboxes/{id}/endpoints/{port}` | GET | Get sandbox endpoint for a port |
| `/sandboxes/{id}/metrics` | GET | Metrics snapshot |
| `/sandboxes/{id}/metrics/watch` | GET | Live metrics over SSE |

Errors return a uniform body: `{ "code": "...", "message": "..." }`.

### Docker

```bash
cp .env.example .env   # set OPENSANDBOX_DOMAIN / OPENSANDBOX_API_KEY
docker compose up -d
```

### Testing

```bash
dotnet test tests/OpenSandbox.Web.Tests
cd frontend && npm test && npm run lint
```

### Screenshots

![Screenshot 1](./screenshots/1.jpg)

![Screenshot 2](./screenshots/2.jpg)

![Screenshot 3](./screenshots/3.jpg)

### License

MIT License

---

<a name="中文"></a>
## 中文

### 项目简介

OpenSandbox Admin 是 **OpenSandbox** 的现代化 Web 管理控制台——一个轻量级、安全的沙箱环境编排平台。该管理面板提供完整的界面来管理容器化沙箱实例、监控资源使用情况以及控制沙箱生命周期。

仓库采用前后端分离结构：React 单页应用 + .NET 10 API（封装官方 [Alibaba.OpenSandbox](https://www.nuget.org/packages/Alibaba.OpenSandbox) SDK）。

### 功能特性

- **仪表盘概览** - 实时统计沙箱状态（运行中、已暂停、创建中、失败）
- **沙箱管理** - 从容器镜像创建单个或批量沙箱；暂停、恢复、删除实例；按状态或元数据过滤和搜索
- **实时监控** - CPU 与内存指标经后端代理以 SSE 推送，浏览器无需直连沙箱端点
- **TTL 管理** - 通过时间选择器延长沙箱过期时间
- **批量操作** - JSON 导入或手动输入批量创建，带进度显示
- **单进程部署** - 后端托管前端构建产物并提供 SPA fallback

### 技术栈

| 领域 | 技术 |
|------|------|
| 前端 | React 19、TypeScript、Vite、Ant Design 5、TanStack Query、ECharts、axios、dayjs |
| 后端 | .NET 10、ASP.NET Core Minimal API、Alibaba.OpenSandbox 1.1.1 |
| 测试 | xUnit + WebApplicationFactory（后端）、Vitest + Testing Library（前端） |

### 仓库结构

```
├── backend/       # .NET 10 API（Contracts / Host / Infrastructure 分层）
├── frontend/      # React 19 + Vite + Ant Design 单页应用
├── tests/         # xUnit 测试项目
├── Dockerfile     # 多阶段构建：node 构建 -> dotnet 发布 -> 运行镜像
└── docker-compose.yml
```

### 快速开始

环境要求：.NET SDK 10+、Node.js 20+

```bash
# 终端 1 - 后端（http://localhost:5080）
cd backend
dotnet run

# 终端 2 - 前端开发服务器（http://localhost:3000，/api 代理到 5080）
cd frontend
npm install
npm run dev
```

### 配置

后端配置位于 `backend/appsettings.json`：

```json
{
  "OpenSandbox": {
    "Domain": "https://sandbox.example.com",
    "ApiKey": "your-api-key"
  }
}
```

### API

所有端点以 `/api/v1` 为前缀：

| 端点 | 方法 | 说明 |
|------|------|------|
| `/health` | GET | 存活检查 |
| `/sandboxes` | GET | 沙箱列表（分页、状态/元数据过滤） |
| `/sandboxes` | POST | 创建沙箱 |
| `/sandboxes/{id}` | GET | 沙箱详情 |
| `/sandboxes/{id}` | DELETE | 删除沙箱 |
| `/sandboxes/{id}/pause` | POST | 暂停沙箱 |
| `/sandboxes/{id}/resume` | POST | 恢复沙箱 |
| `/sandboxes/{id}/renew-expiration` | POST | 延长过期时间 |
| `/sandboxes/{id}/endpoints/{port}` | GET | 获取沙箱端口端点 |
| `/sandboxes/{id}/metrics` | GET | 指标快照 |
| `/sandboxes/{id}/metrics/watch` | GET | SSE 实时指标 |

错误响应统一为：`{ "code": "...", "message": "..." }`。

### Docker

```bash
cp .env.example .env   # 设置 OPENSANDBOX_DOMAIN / OPENSANDBOX_API_KEY
docker compose up -d
```

### 测试

```bash
dotnet test tests/OpenSandbox.Web.Tests
cd frontend && npm test && npm run lint
```

### 截图

![截图 1](./screenshots/1.jpg)

![截图 2](./screenshots/2.jpg)

![截图 3](./screenshots/3.jpg)

### 许可证

MIT License
```

- [ ] **Step 2: 自检**

Run: `cd /e/GitHub/OpenSandBoxWeb && git status`
Expected: 仅 README.md 修改

- [ ] **Step 3: 提交**

```bash
git add README.md
git commit -m "docs: rewrite README for the new frontend/backend architecture

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

- [ ] **Step 4: 更新截图（手动，可选延后）**

启动完整环境后（backend 5080 + 前端构建产物），打开 http://localhost:5080/ 分别截取 Dashboard、沙箱列表、沙箱详情三页，替换 `screenshots/1.jpg`、`2.jpg`、`3.jpg`。旧截图展示的是 Vue 版 UI，务必在合并前更新。

