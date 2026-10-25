# Console Keycloak Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把现有 OpenSandbox 控制台整体放到 Keycloak OIDC 登录之后，管理 API 默认拒绝匿名访问，前端按沙箱/卷两个分面的 admin/viewer 角色显示可用功能。

**Architecture:** 后端用 JwtBearer 校验 Keycloak access token，从 `resource_access.<clientId>.roles` 解析角色，再用一个全局路径 RBAC 中间件按方法/路径执行矩阵；SPA 走授权码 + PKCE（S256），token 只放 sessionStorage，realm/client 由后端在 index.html 运行时注入。控制台身份通过 `/api/v1/console/me` 暴露给前端。

**Tech Stack:** .NET 10 Minimal APIs、Microsoft.AspNetCore.Authentication.JwtBearer 10.0.10、xUnit + WebApplicationFactory；React 19、Vite 8、axios、`@noble/hashes` 2.4.0、Vitest 5。

**Spec:** `docs/superpowers/specs/2026-10-25-global-volume-manager-design.md`（§4.1 FR-13、§6.4、§6.10–§6.12、ADR-11–13）

## Global Constraints

- 后端 `net10.0`、nullable + implicit usings；前端 TypeScript 使用 `verbatimModuleSyntax` 与 `erasableSyntaxOnly`（不要用 enum、构造函数参数属性）。
- JwtBearer：`MapInboundClaims=false`、`ValidateAudience=false`、校验 `azp == Auth:ClientId`、`NameClaimType=preferred_username`、`ClockSkew=30s`、`ValidateIssuer=true`。
- `Auth:Authority` 统一去除尾部 `/`；缺失 `Auth:Authority` 或 `Auth:ClientId` 时启动直接抛异常。
- 角色只读 `resource_access.<Auth:ClientId>.roles`，绝不读 `realm_access`；`sandbox:admin` 隐含 `sandbox:viewer`，`volume:admin` 隐含 `volume:viewer`；两个分面互不隐含。
- 默认拒绝：匿名访问 `/api/*` → 401；已登录但缺角色或路径未登记 → 403；`/api/v1/health`、`/healthz`、`/readyz`、静态资源、SPA fallback 匿名。
- 删除 `AdminApiKey` 概念，不新增任何共享管理密钥。
- access/refresh/id token 只存 `sessionStorage`；URL 中只允许出现 OIDC 的 `code`/`state`，不允许出现 token。
- 生产前端不得回落到构建时 realm；`index.html` 响应必须 `Cache-Control: no-store`。
- 健康检查与 SPA fallback 必须显式 `AllowAnonymous()`，否则 `FallbackPolicy` 会拦截。
- 所有命令默认在仓库根目录执行；前端命令在 `frontend/`。
- 用户可见文案沿用现有中文风格。

## Scope

- 本计划覆盖 spec 的 PR 1–2：控制台认证与前端角色门控。
- 卷治理（契约、持久化、扫描、租户/管理 API、保护清单、卷页面、运维手册）在 `docs/superpowers/plans/2026-10-25-global-volume-manager.md`（下一份计划）。
- 租户 API Key 中间件**不在本计划**实现：它的 Key 表随卷治理持久化任务落地，因此由卷治理计划的租户 API 任务实现（spec §6.4 与 PR 6 已含“Key 鉴权”）。本计划只保证 `/api/v1/tenant` 被控制台 RBAC 跳过，交给后续中间件。
- 本计划会解析并展示卷角色，但不提供卷页面。只有卷角色的账号在本计划完成后会看到“卷管理界面将在后续版本提供”的无权限页；卷治理计划会替换这个门控。

## File Structure

**Backend**

| 文件 | 责任 |
| --- | --- |
| `backend/OpenSandbox.Web.csproj` | 增加 JwtBearer 包 |
| `backend/Host/Security/ConsoleAuthOptions.cs` | `Auth` 配置模型 |
| `backend/Host/Security/ConsoleAuth.cs` | JwtBearer 注册、azp 校验、JSON 401/403、FallbackPolicy |
| `backend/Host/Security/ConsoleRoles.cs` | 角色常量、`resource_access` 解析、admin→viewer 判定 |
| `backend/Host/Security/ConsoleRbac.cs` | 纯函数 `Decide` + 全局 RBAC 中间件 |
| `backend/Host/Security/AuthConfigInjection.cs` | 向 index.html 注入 `window.__OPENSANDBOX_AUTH__` |
| `backend/Host/Web/ConsoleEndpoints.cs` | `GET /api/v1/console/me` |
| `backend/Host/Web/SpaFallbackEndpoints.cs` | 匿名静态托管、注入 index、`/api` 不走 SPA |
| `backend/Host/Web/HealthEndpoints.cs` | 健康检查显式匿名 |
| `backend/Program.cs` | 注册认证、RBAC、路由与中间件顺序 |
| `backend/appsettings.json` / `appsettings.Development.json` | Auth 配置 |

**Backend tests**

| 文件 | 责任 |
| --- | --- |
| `tests/OpenSandbox.Web.Tests/Endpoints/TestAuthHandler.cs` | 测试认证方案（`X-Test-User`/`X-Test-Roles`） |
| `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs` | 默认已登录 admin；匿名/指定角色客户端 |
| `tests/OpenSandbox.Web.Tests/Security/ConsoleRolesTests.cs` | 角色解析与隐含关系 |
| `tests/OpenSandbox.Web.Tests/Security/ConsoleAuthTests.cs` | JwtBearer 配置、缺失配置、azp |
| `tests/OpenSandbox.Web.Tests/Security/ConsoleRbacTests.cs` | 路径/方法 → 决策矩阵 |
| `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleEndpointsTests.cs` | `/console/me` 行为 |
| `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleAccessTests.cs` | 401/403/健康检查集成行为 |
| `tests/OpenSandbox.Web.Tests/Web/AuthConfigInjectionTests.cs` | HTML 注入、转义、缺少 `</head>` |

**Frontend**

| 文件 | 责任 |
| --- | --- |
| `frontend/package.json` / `package-lock.json` | 增加 `@noble/hashes` |
| `frontend/src/auth/pkce.ts` | base64url、verifier、S256 challenge |
| `frontend/src/auth/authModel.ts` | 运行时配置解析、URL 构造、回调解析、刷新阈值 |
| `frontend/src/auth/auth.ts` | sessionStorage、登录事务、换票、单飞续签、启动门控 |
| `frontend/src/auth/roles.ts` | 前端角色隐含关系 |
| `frontend/src/auth/AuthContext.tsx` | 拉取 `/console/me`，提供 `me`/`can` |
| `frontend/src/auth/AuthSplash.tsx` / `AuthFailure.tsx` / `NoPermission.tsx` | 启动、失败、无权限界面 |
| `frontend/src/auth/RequireSandboxAdmin.tsx` | 写页面路由守卫 |
| `frontend/src/api/console.ts` | `/console/me` 客户端 |
| `frontend/src/api/client.ts` | Bearer 注入、401 重新登录 |
| `frontend/src/main.tsx` | 启动门控 + AuthProvider |
| `frontend/src/components/layout/AppLayout.tsx` | 角色菜单、用户菜单、注销 |
| `frontend/src/pages/SandboxList.tsx` / `SandboxDetail.tsx` | 写操作门控 |
| `frontend/src/pages/Settings.tsx` | 账号、角色、realm/client 展示 |
| `frontend/src/router/index.tsx` | admin 路由守卫 |
| `frontend/src/hooks/useMetricsStream.ts` | SSE 携带 Bearer |

**Frontend tests**

| 文件 | 责任 |
| --- | --- |
| `frontend/src/auth/__tests__/pkce.test.ts` | RFC 7636 向量 |
| `frontend/src/auth/__tests__/authModel.test.ts` | 配置/URL/回调/阈值 |
| `frontend/src/auth/__tests__/auth.test.ts` | 登录事务、换票、重放、单飞续签、returnTo 清洗 |
| `frontend/src/auth/__tests__/roles.test.ts` | 分面角色 |
| `frontend/src/components/layout/__tests__/AppLayout.test.tsx` | 补 `useAuth` mock |
| `frontend/src/pages/__tests__/SandboxList.test.tsx` | 补 `useAuth` mock |

**Deployment / docs**

`docker-compose.yml`、`.env.example`、`frontend/.env.example`、`README.md`。

---

### Task 1: 后端 JWT 身份、角色解析与 `/console/me`

**Files:**

- Modify: `backend/OpenSandbox.Web.csproj`
- Create: `backend/Host/Security/ConsoleAuthOptions.cs`
- Create: `backend/Host/Security/ConsoleAuth.cs`
- Create: `backend/Host/Security/ConsoleRoles.cs`
- Create: `backend/Host/Web/ConsoleEndpoints.cs`
- Modify: `backend/Host/Web/HealthEndpoints.cs`
- Modify: `backend/Host/Web/SpaFallbackEndpoints.cs`
- Modify: `backend/Program.cs`
- Modify: `backend/appsettings.json`
- Modify: `backend/appsettings.Development.json`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/TestAuthHandler.cs`
- Modify: `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`
- Create: `tests/OpenSandbox.Web.Tests/Security/ConsoleRolesTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Security/ConsoleAuthTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleEndpointsTests.cs`

**Interfaces:**

- Produces: `ConsoleAuthOptions { string Authority; string ClientId; bool RequireHttpsMetadata; }`
- Produces: `ConsoleRoles.SandboxAdmin|SandboxViewer|VolumeAdmin|VolumeViewer`、`IReadOnlySet<string> RolesOf(ClaimsPrincipal, string clientId)`、`bool Has(IReadOnlySet<string>, string required)`
- Produces: `ConsoleAuth.AddConsoleAuth(IServiceCollection, IConfiguration)`、`ConsoleAuth.AzpMatches(ClaimsPrincipal?, string clientId)`
- Produces: `ConsoleEndpoints.MapConsoleEndpoints(IEndpointRouteBuilder)`、`ConsoleEndpoints.ConsoleMeResponse(string Subject, string Username, IReadOnlyList<string> Roles)`
- Produces (tests): `TestAuthHandler.SchemeName/UserHeader/RolesHeader`、`ApiFactory.CreateClientWithRoles(params string[])`、`ApiFactory.CreateAnonymousClient()`

- [ ] **Step 1: 增加 JwtBearer 包并写角色解析失败测试**

在 `backend/OpenSandbox.Web.csproj` 的 `ItemGroup` 中加入：

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.10" />
```

新建 `tests/OpenSandbox.Web.Tests/Security/ConsoleRolesTests.cs`：

```csharp
using System.Security.Claims;
using System.Text.Json;
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleRolesTests
{
    private const string ClientId = "opensandbox-web";

    private static ClaimsPrincipal PrincipalWithResourceAccess(object resourceAccess)
    {
        var json = JsonSerializer.Serialize(resourceAccess);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim("resource_access", json),
        ], "Test"));
    }

    [Fact]
    public void RolesOf_ReadsOnlyDedicatedClientRoles()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            ["account"] = new { roles = new[] { "sandbox:admin" } },
            [ClientId] = new { roles = new[] { "volume:viewer" } },
        });

        var roles = ConsoleRoles.RolesOf(principal, ClientId);

        Assert.Equal(new[] { "volume:viewer" }, roles);
    }

    [Fact]
    public void RolesOf_MissingResourceAccess_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_MalformedJson_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("resource_access", "{not json")], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_RolesNotArray_ReturnsEmpty()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            [ClientId] = new { roles = "sandbox:admin" },
        });

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_DropsUnknownRoles()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            [ClientId] = new { roles = new[] { "sandbox:admin", "realm-admin", "volume:viewer" } },
        });

        var roles = ConsoleRoles.RolesOf(principal, ClientId);

        Assert.True(roles.SetEquals(new[] { "sandbox:admin", "volume:viewer" }));
    }

    [Theory]
    [InlineData("sandbox:admin", "sandbox:viewer", true)]
    [InlineData("sandbox:viewer", "sandbox:admin", false)]
    [InlineData("volume:admin", "volume:viewer", true)]
    [InlineData("volume:viewer", "volume:admin", false)]
    [InlineData("sandbox:admin", "volume:viewer", false)]
    [InlineData("volume:admin", "sandbox:viewer", false)]
    [InlineData("sandbox:admin", "sandbox:admin", true)]
    public void Has_AppliesFacetHierarchy(string granted, string required, bool expected)
    {
        var roles = new HashSet<string> { granted };

        Assert.Equal(expected, ConsoleRoles.Has(roles, required));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleRolesTests"`

Expected: 编译失败，提示 `OpenSandbox.Web.Host.Security.ConsoleRoles` 不存在。

- [ ] **Step 3: 实现角色解析**

新建 `backend/Host/Security/ConsoleRoles.cs`：

```csharp
using System.Security.Claims;
using System.Text.Json;

namespace OpenSandbox.Web.Host.Security;

public static class ConsoleRoles
{
    public const string SandboxAdmin = "sandbox:admin";
    public const string SandboxViewer = "sandbox:viewer";
    public const string VolumeAdmin = "volume:admin";
    public const string VolumeViewer = "volume:viewer";

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        SandboxAdmin,
        SandboxViewer,
        VolumeAdmin,
        VolumeViewer,
    };

    public static IReadOnlySet<string> RolesOf(ClaimsPrincipal principal, string clientId)
    {
        var raw = principal.FindFirst("resource_access")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return Empty;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty(clientId, out var client) ||
                !client.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return Empty;

            var parsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind != JsonValueKind.String)
                    continue;

                var value = role.GetString();
                if (value is not null && Known.Contains(value))
                    parsed.Add(value);
            }

            return parsed;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static bool Has(IReadOnlySet<string> roles, string required) => required switch
    {
        SandboxViewer => roles.Contains(SandboxAdmin) || roles.Contains(SandboxViewer),
        VolumeViewer => roles.Contains(VolumeAdmin) || roles.Contains(VolumeViewer),
        _ => roles.Contains(required),
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleRolesTests"`

Expected: 全部通过。

- [ ] **Step 5: 写认证配置失败测试**

新建 `tests/OpenSandbox.Web.Tests/Security/ConsoleAuthTests.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleAuthTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    [Fact]
    public void AddConsoleAuth_RequiresAuthorityAndClientId()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddConsoleAuth(Config()));
    }

    [Fact]
    public void AddConsoleAuth_ConfiguresJwtBearer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConsoleAuth(Config(
            ("Auth:Authority", "https://keycloak.example/realms/dev/"),
            ("Auth:ClientId", "opensandbox-web"),
            ("Auth:RequireHttpsMetadata", "false")));

        using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal("https://keycloak.example/realms/dev", jwt.Authority);
        Assert.False(jwt.RequireHttpsMetadata);
        Assert.False(jwt.MapInboundClaims);
        Assert.True(jwt.TokenValidationParameters.ValidateIssuer);
        Assert.Equal("https://keycloak.example/realms/dev", jwt.TokenValidationParameters.ValidIssuer);
        Assert.False(jwt.TokenValidationParameters.ValidateAudience);
        Assert.True(jwt.TokenValidationParameters.ValidateLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), jwt.TokenValidationParameters.ClockSkew);
        Assert.Equal("preferred_username", jwt.TokenValidationParameters.NameClaimType);
    }

    [Theory]
    [InlineData("opensandbox-web", true)]
    [InlineData("another-client", false)]
    [InlineData(null, false)]
    public void AzpMatches_RequiresExactClient(string? azp, bool expected)
    {
        var identity = new ClaimsIdentity("Test");
        if (azp is not null)
            identity.AddClaim(new Claim("azp", azp));
        var principal = new ClaimsPrincipal(identity);

        Assert.Equal(expected, ConsoleAuth.AzpMatches(principal, "opensandbox-web"));
    }

    [Fact]
    public void AzpMatches_MissingPrincipal_IsFalse()
        => Assert.False(ConsoleAuth.AzpMatches(null, "opensandbox-web"));
}
```

- [ ] **Step 6: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleAuthTests"`

Expected: 编译失败，提示 `ConsoleAuth` / `ConsoleAuthOptions` 不存在。

- [ ] **Step 7: 实现认证注册与配置**

新建 `backend/Host/Security/ConsoleAuthOptions.cs`：

```csharp
namespace OpenSandbox.Web.Host.Security;

public sealed class ConsoleAuthOptions
{
    public const string SectionName = "Auth";

    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;
}
```

新建 `backend/Host/Security/ConsoleAuth.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace OpenSandbox.Web.Host.Security;

public static class ConsoleAuth
{
    public static IServiceCollection AddConsoleAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ConsoleAuthOptions.SectionName).Get<ConsoleAuthOptions>()
            ?? new ConsoleAuthOptions();

        if (string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException("缺少配置 Auth:Authority（Keycloak realm 地址）");
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new InvalidOperationException("缺少配置 Auth:ClientId（控制台 OIDC 客户端 ID）");

        options.Authority = options.Authority.TrimEnd('/');

        services.Configure<ConsoleAuthOptions>(configured =>
        {
            configured.Authority = options.Authority;
            configured.ClientId = options.ClientId;
            configured.RequireHttpsMetadata = options.RequireHttpsMetadata;
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = options.Authority;
            jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Authority,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "preferred_username",
            };
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (!AzpMatches(context.Principal, options.ClientId))
                        context.Fail("令牌不是签发给控制台客户端的");
                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.Headers["WWW-Authenticate"] = "Bearer";
                    await context.Response.WriteAsJsonAsync(new { code = "unauthorized", message = "需要登录" });
                },
                OnForbidden = async context =>
                {
                    var roles = ConsoleRoles
                        .RolesOf(context.HttpContext.User, options.ClientId)
                        .OrderBy(role => role, StringComparer.Ordinal)
                        .ToArray();
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "forbidden",
                        message = "没有访问权限",
                        roles,
                    });
                },
            };
        });

        services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    public static bool AzpMatches(ClaimsPrincipal? principal, string clientId) =>
        string.Equals(principal?.FindFirst("azp")?.Value, clientId, StringComparison.Ordinal);
}
```

修改 `backend/appsettings.json`，在 `OpenSandbox` 后加入：

```json
"Auth": {
  "Authority": "",
  "ClientId": "",
  "RequireHttpsMetadata": true
}
```

修改 `backend/appsettings.Development.json`，加入开发 realm：

```json
"Auth": {
  "Authority": "https://dev-passport.ai4c.cn/realms/ai4cbrain",
  "ClientId": "opensandbox-web",
  "RequireHttpsMetadata": false
}
```

- [ ] **Step 8: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleAuthTests"`

Expected: 全部通过。

- [ ] **Step 9: 写测试身份、`/console/me` 失败测试，并让现有测试默认已登录**

新建 `tests/OpenSandbox.Web.Tests/Endpoints/TestAuthHandler.cs`：

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var clientId = Context.RequestServices
            .GetRequiredService<IOptions<ConsoleAuthOptions>>().Value.ClientId;
        var roles = Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resourceAccess = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [clientId] = new { roles },
        });

        var claims = new[]
        {
            new Claim("sub", $"user-{user}"),
            new Claim("preferred_username", user),
            new Claim("azp", clientId),
            new Claim("resource_access", resourceAccess),
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { code = "unauthorized", message = "需要登录" });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { code = "forbidden", message = "没有访问权限" });
    }
}
```

把 `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs` 整体替换为：

```csharp
using Microsoft.AspNetCore.Authentication;
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
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        });
    }

    public new HttpClient CreateClient() =>
        CreateClientWithRoles("sandbox:admin", "volume:admin");

    public HttpClient CreateClientWithRoles(params string[] roles)
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "test-admin");
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    public HttpClient CreateAnonymousClient() => base.CreateClient();
}
```

新建 `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Host.Web;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ConsoleEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ConsoleEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Me_ReturnsUsernameAndKnownRolesOnly()
    {
        var client = _factory.CreateClientWithRoles("sandbox:admin", "volume:viewer", "realm-admin");

        var response = await client.GetAsync("/api/v1/console/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsoleEndpoints.ConsoleMeResponse>();
        Assert.Equal("user-test-admin", body!.Subject);
        Assert.Equal("test-admin", body.Username);
        Assert.Equal(new[] { "sandbox:admin", "volume:viewer" }, body.Roles);
    }

    [Fact]
    public async Task Me_WithoutRoles_ReturnsEmptyRoleList()
    {
        var client = _factory.CreateClientWithRoles();

        var response = await client.GetAsync("/api/v1/console/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsoleEndpoints.ConsoleMeResponse>();
        Assert.Empty(body!.Roles);
    }

    [Fact]
    public async Task Me_Anonymous_Returns401Json()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/v1/console/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("unauthorized", body!.Code);
    }
}
```

- [ ] **Step 10: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleEndpointsTests"`

Expected: 失败——`/api/v1/console/me` 返回 404；现有端点测试还会因为 `FallbackPolicy` 尚未配置测试方案而失败。

- [ ] **Step 11: 实现 `/console/me`、注册认证并放行匿名健康检查与 SPA fallback**

新建 `backend/Host/Web/ConsoleEndpoints.cs`：

```csharp
using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;

namespace OpenSandbox.Web.Host.Web;

public static class ConsoleEndpoints
{
    public sealed record ConsoleMeResponse(
        string Subject,
        string Username,
        IReadOnlyList<string> Roles);

    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/console").WithTags("Console");

        group.MapGet("/me", (ClaimsPrincipal user, IOptions<ConsoleAuthOptions> options) =>
        {
            var subject = user.FindFirst("sub")?.Value ?? "";
            var preferredUsername = user.FindFirst("preferred_username")?.Value;
            var username = string.IsNullOrWhiteSpace(preferredUsername)
                ? (subject.Length > 8 ? subject[..8] : subject)
                : preferredUsername;
            var roles = ConsoleRoles.RolesOf(user, options.Value.ClientId)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray();

            return Results.Ok(new ConsoleMeResponse(subject, username, roles));
        });

        return app;
    }
}
```

修改 `backend/Host/Web/HealthEndpoints.cs`，在 `MapGet` 末尾加 `AllowAnonymous()`：

```csharp
app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "ok",
    version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0",
})).AllowAnonymous();
```

修改 `backend/Host/Web/SpaFallbackEndpoints.cs` 的最后一行：

```csharp
app.MapFallbackToFile("index.html").AllowAnonymous();
```

把 `backend/Program.cs` 整体替换为：

```csharp
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

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapSandboxEndpoints();
app.MapMetricsEndpoints();
app.MapConsoleEndpoints();
app.MapSpaFallback();

app.Run();

public partial class Program;
```

- [ ] **Step 12: 运行全部后端测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过；现有 `SandboxEndpointsTests`/`MetricsEndpointsTests`/`HealthEndpointsTests` 使用 `ApiFactory` 的默认 admin 身份。

- [ ] **Step 13: 提交**

```bash
git add backend/OpenSandbox.Web.csproj backend/Host/Security/ConsoleAuthOptions.cs backend/Host/Security/ConsoleAuth.cs backend/Host/Security/ConsoleRoles.cs backend/Host/Web/ConsoleEndpoints.cs backend/Host/Web/HealthEndpoints.cs backend/Host/Web/SpaFallbackEndpoints.cs backend/Program.cs backend/appsettings.json backend/appsettings.Development.json tests/OpenSandbox.Web.Tests/Endpoints/TestAuthHandler.cs tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs tests/OpenSandbox.Web.Tests/Endpoints/ConsoleEndpointsTests.cs tests/OpenSandbox.Web.Tests/Security/ConsoleRolesTests.cs tests/OpenSandbox.Web.Tests/Security/ConsoleAuthTests.cs
git commit -m "feat(auth): validate Keycloak console tokens" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 全局路径 RBAC 中间件

**Files:**

- Create: `backend/Host/Security/ConsoleRbac.cs`
- Modify: `backend/Program.cs`
- Create: `tests/OpenSandbox.Web.Tests/Security/ConsoleRbacTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleAccessTests.cs`

**Interfaces:**

- Consumes: `ConsoleRoles`、`ConsoleAuthOptions`（Task 1）
- Produces: `RbacAction { Skip, AnyAuthenticated, RequireRole, Deny }`、`RbacDecision(RbacAction Action, string? Role)`、`ConsoleRbac.Decide(PathString path, string method)`、`IApplicationBuilder.UseConsoleRbac()`

- [ ] **Step 1: 写路径/方法决策失败测试**

新建 `tests/OpenSandbox.Web.Tests/Security/ConsoleRbacTests.cs`：

```csharp
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleRbacTests
{
    [Theory]
    [InlineData("/api/v1/health", "GET", RbacAction.Skip, null)]
    [InlineData("/healthz", "GET", RbacAction.Skip, null)]
    [InlineData("/readyz", "GET", RbacAction.Skip, null)]
    [InlineData("/dashboard", "GET", RbacAction.Skip, null)]
    [InlineData("/api/v1/tenant/volumes", "GET", RbacAction.Skip, null)]
    [InlineData("/api/v1/sandboxes", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes", "HEAD", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes", "POST", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1", "DELETE", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1/endpoints/8080", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1/metrics", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes/sb-1/metrics/watch", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/volumes", "GET", RbacAction.RequireRole, ConsoleRoles.VolumeViewer)]
    [InlineData("/api/v1/volumes/vol-1", "PATCH", RbacAction.RequireRole, ConsoleRoles.VolumeAdmin)]
    [InlineData("/api/v1/volume-tenants", "GET", RbacAction.RequireRole, ConsoleRoles.VolumeViewer)]
    [InlineData("/api/v1/volume-tenants", "POST", RbacAction.RequireRole, ConsoleRoles.VolumeAdmin)]
    [InlineData("/api/v1/console/me", "GET", RbacAction.AnyAuthenticated, null)]
    [InlineData("/api/v1/unknown", "GET", RbacAction.Deny, null)]
    [InlineData("/api/v1/sandboxes", "OPTIONS", RbacAction.Skip, null)]
    public void Decide_MapsPathAndMethod(string path, string method, RbacAction action, string? role)
    {
        var decision = ConsoleRbac.Decide(new PathString(path), method);

        Assert.Equal(action, decision.Action);
        Assert.Equal(role, decision.Role);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleRbacTests"`

Expected: 编译失败，提示 `ConsoleRbac` / `RbacAction` 不存在。

- [ ] **Step 3: 实现决策函数与中间件**

新建 `backend/Host/Security/ConsoleRbac.cs`：

```csharp
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
```

- [ ] **Step 4: 运行决策测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleRbacTests"`

Expected: 全部通过。

- [ ] **Step 5: 写集成测试**

新建 `tests/OpenSandbox.Web.Tests/Endpoints/ConsoleAccessTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ConsoleAccessTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ConsoleAccessTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AnonymousSandboxList_Returns401Json()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/v1/sandboxes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("unauthorized", body!.Code);
    }

    [Fact]
    public async Task Viewer_CanReadButCannotDelete()
    {
        var viewer = _factory.CreateClientWithRoles("sandbox:viewer");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/sandboxes")).StatusCode);

        var delete = await viewer.DeleteAsync("/api/v1/sandboxes/sb-1");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        var body = await delete.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("forbidden", body!.Code);
    }

    [Fact]
    public async Task NoRoles_ReadSandboxes_Returns403()
    {
        var response = await _factory.CreateClientWithRoles().GetAsync("/api/v1/sandboxes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotResolveDataPlaneEndpoint()
    {
        var viewer = _factory.CreateClientWithRoles("sandbox:viewer");

        var response = await viewer.GetAsync("/api/v1/sandboxes/sb-1/endpoints/8080");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SandboxAdmin_CanResolveDataPlaneEndpoint()
    {
        var admin = _factory.CreateClientWithRoles("sandbox:admin");

        var response = await admin.GetAsync("/api/v1/sandboxes/sb-1/endpoints/8080");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VolumeViewer_CannotReadSandboxApi()
    {
        var response = await _factory.CreateClientWithRoles("volume:viewer")
            .GetAsync("/api/v1/sandboxes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownApi_AuthenticatedAdmin_Returns403()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/not-registered");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousHealth_Returns200()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 6: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ConsoleAccessTests"`

Expected: 失败——viewer 的 DELETE 与 endpoints 读取会返回 204/200，而不是 403（中间件尚未接入）。

- [ ] **Step 7: 接入中间件**

修改 `backend/Program.cs`，在 `UseAuthentication` 与 `UseAuthorization` 之间插入：

```csharp
app.UseAuthentication();
app.UseConsoleRbac();
app.UseAuthorization();
```

- [ ] **Step 8: 运行全部后端测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过。

- [ ] **Step 9: 提交**

```bash
git add backend/Host/Security/ConsoleRbac.cs backend/Program.cs tests/OpenSandbox.Web.Tests/Security/ConsoleRbacTests.cs tests/OpenSandbox.Web.Tests/Endpoints/ConsoleAccessTests.cs
git commit -m "feat(auth): enforce console RBAC by path and method" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 运行时注入登录配置与匿名 SPA 托管

**Files:**

- Create: `backend/Host/Security/AuthConfigInjection.cs`
- Modify: `backend/Host/Web/SpaFallbackEndpoints.cs`
- Modify: `backend/Program.cs`
- Create: `tests/OpenSandbox.Web.Tests/Web/AuthConfigInjectionTests.cs`

**Interfaces:**

- Consumes: `ConsoleAuthOptions`（Task 1）
- Produces: `AuthConfigInjection.GlobalName = "__OPENSANDBOX_AUTH__"`、`string Inject(string html, ConsoleAuthOptions options)`

- [ ] **Step 1: 写注入测试**

新建 `tests/OpenSandbox.Web.Tests/Web/AuthConfigInjectionTests.cs`：

```csharp
using OpenSandbox.Web.Host.Security;
using OpenSandbox.Web.Host.Web;
using Xunit;

namespace OpenSandbox.Web.Tests.Web;

public sealed class AuthConfigInjectionTests
{
    private static readonly ConsoleAuthOptions Options = new()
    {
        Authority = "https://keycloak.example/realms/dev",
        ClientId = "opensandbox-web",
    };

    [Fact]
    public void Inject_AddsAuthConfigBeforeHeadEnd()
    {
        var html = "<!doctype html><html><head><title>x</title></head><body></body></html>";

        var injected = AuthConfigInjection.Inject(html, Options);

        Assert.Contains(
            "<script>window.__OPENSANDBOX_AUTH__={\"authority\":\"https://keycloak.example/realms/dev\",\"clientId\":\"opensandbox-web\"};</script></head>",
            injected);
    }

    [Fact]
    public void Inject_EscapesHtmlSensitiveCharacters()
    {
        var options = new ConsoleAuthOptions
        {
            Authority = "https://x/</script><script>",
            ClientId = "c",
        };

        var injected = AuthConfigInjection.Inject("<head></head>", options);

        Assert.DoesNotContain("</script><script>", injected);
        Assert.Contains("\\u003C", injected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_ThrowsWhenHeadMissing()
    {
        Assert.Throws<InvalidOperationException>(
            () => AuthConfigInjection.Inject("<html></html>", Options));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~AuthConfigInjectionTests"`

Expected: 编译失败，提示 `AuthConfigInjection` 不存在。

- [ ] **Step 3: 实现注入**

新建 `backend/Host/Security/AuthConfigInjection.cs`：

```csharp
using System.Text.Json;

namespace OpenSandbox.Web.Host.Security;

public static class AuthConfigInjection
{
    public const string GlobalName = "__OPENSANDBOX_AUTH__";

    public static string Inject(string html, ConsoleAuthOptions options)
    {
        var payload = JsonSerializer.Serialize(new
        {
            authority = options.Authority,
            clientId = options.ClientId,
        });
        var script = $"<script>window.{GlobalName}={payload};</script>";

        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
            throw new InvalidOperationException("index.html 缺少 </head>，无法注入登录配置");

        return html.Insert(headEnd, script);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~AuthConfigInjectionTests"`

Expected: 全部通过。

- [ ] **Step 5: 重写 SPA 托管并调整顺序**

把 `backend/Host/Web/SpaFallbackEndpoints.cs` 整体替换为：

```csharp
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
```

修改 `backend/Program.cs`：把 `app.MapSpaFallback();` 移到 `UseAuthentication` 之前，并在 `SandboxExceptionMiddleware` 后加注释。最终顺序为：

```csharp
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
```

- [ ] **Step 6: 运行全部后端测试**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过。

- [ ] **Step 7: 手工验证注入与 no-store**

Run（先构建前端，再启动后端；后端保持运行）：

```bash
cd frontend
npm run build
cd ../backend
dotnet run
```

另开终端：

```bash
curl -s http://localhost:5080/ | grep -o '__OPENSANDBOX_AUTH__[^<]*'
curl -sI http://localhost:5080/ | grep -i '^cache-control'
curl -s http://localhost:5080/sandboxes | grep -o '__OPENSANDBOX_AUTH__[^<]*'
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/api/v1/not-registered
```

Expected：前三条都输出包含 `__OPENSANDBOX_AUTH__` 的配置；`cache-control: no-store`；最后一条输出 `401`。完成后在运行 `dotnet run` 的终端按 `Ctrl+C`。

- [ ] **Step 8: 提交**

```bash
git add backend/Host/Security/AuthConfigInjection.cs backend/Host/Web/SpaFallbackEndpoints.cs backend/Program.cs tests/OpenSandbox.Web.Tests/Web/AuthConfigInjectionTests.cs
git commit -m "feat(auth): inject runtime auth config into the console" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 前端 PKCE 与认证模型

**Files:**

- Modify: `frontend/package.json`、`frontend/package-lock.json`
- Create: `frontend/src/auth/pkce.ts`
- Create: `frontend/src/auth/authModel.ts`
- Create: `frontend/src/auth/__tests__/pkce.test.ts`
- Create: `frontend/src/auth/__tests__/authModel.test.ts`

**Interfaces:**

- Produces: `base64Url(Uint8Array): string`、`createVerifier(randomBytes?: Uint8Array): string`、`challengeFor(verifier: string): string`、`createPkce(): { verifier: string; challenge: string }`
- Produces: `AuthConfig { authority: string; clientId: string }`、`readInjectedAuth()`、`resolveAuthConfig(env?)`、`tokenEndpoint(config)`、`buildAuthUrl(config, redirectUri, state, challenge)`、`buildLogoutUrl(config, redirectUri, idToken)`、`parseCallback(search)`、`needsRefresh(expiresAtMs, nowMs)`、`randomState()`、`AuthorizationRejected`

- [ ] **Step 1: 安装依赖**

Run（在 `frontend/`）: `npm install "@noble/hashes@^2.4.0"`

Expected：`package.json` 的 `dependencies` 出现 `@noble/hashes`，锁文件更新。

- [ ] **Step 2: 写 PKCE 失败测试**

新建 `frontend/src/auth/__tests__/pkce.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { base64Url, challengeFor, createPkce, createVerifier } from '../pkce'

describe('pkce', () => {
  it('matches the RFC 7636 Appendix B vector', () => {
    expect(challengeFor('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe(
      'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
    )
  })

  it('creates a 43-character base64url verifier', () => {
    expect(createVerifier()).toMatch(/^[A-Za-z0-9_-]{43}$/)
  })

  it('returns a matching challenge pair', () => {
    const { verifier, challenge } = createPkce()
    expect(challenge).toBe(challengeFor(verifier))
    expect(challenge).toMatch(/^[A-Za-z0-9_-]{43}$/)
  })

  it('encodes bytes without padding or unsafe characters', () => {
    expect(base64Url(new Uint8Array([251, 255]))).toBe('-_8')
  })
})
```

- [ ] **Step 3: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/pkce.test.ts`

Expected: 失败，提示无法解析 `../pkce`。

- [ ] **Step 4: 实现 PKCE**

新建 `frontend/src/auth/pkce.ts`：

```ts
import { sha256 } from '@noble/hashes/sha2.js'

export function base64Url(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function createVerifier(randomBytes?: Uint8Array): string {
  return base64Url(randomBytes ?? crypto.getRandomValues(new Uint8Array(32)))
}

export function challengeFor(verifier: string): string {
  return base64Url(sha256(new TextEncoder().encode(verifier)))
}

export function createPkce(): { verifier: string; challenge: string } {
  const verifier = createVerifier()
  return { verifier, challenge: challengeFor(verifier) }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/pkce.test.ts`

Expected: 全部通过。

- [ ] **Step 6: 写认证模型失败测试**

新建 `frontend/src/auth/__tests__/authModel.test.ts`：

```ts
import { afterEach, describe, expect, it } from 'vitest'
import {
  AuthorizationRejected,
  buildAuthUrl,
  buildLogoutUrl,
  needsRefresh,
  parseCallback,
  randomState,
  resolveAuthConfig,
} from '../authModel'

const config = { authority: 'https://kc.example/realms/dev', clientId: 'opensandbox-web' }

type Injected = { authority?: string; clientId?: string }

afterEach(() => {
  delete (globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__
})

describe('resolveAuthConfig', () => {
  it('prefers the backend-injected config and trims trailing slashes', () => {
    ;(globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__ = {
      authority: 'https://kc.example/realms/dev/',
      clientId: 'opensandbox-web',
    }

    expect(resolveAuthConfig({ DEV: false })).toEqual(config)
  })

  it('does not fall back to build-time values in production', () => {
    expect(() =>
      resolveAuthConfig({
        DEV: false,
        VITE_AUTH_AUTHORITY: 'https://kc.example/realms/dev',
        VITE_AUTH_CLIENT_ID: 'opensandbox-web',
      }),
    ).toThrow('__OPENSANDBOX_AUTH__')
  })

  it('uses Vite env values in development', () => {
    expect(
      resolveAuthConfig({
        DEV: true,
        VITE_AUTH_AUTHORITY: 'https://kc.example/realms/dev',
        VITE_AUTH_CLIENT_ID: 'opensandbox-web',
      }),
    ).toEqual(config)
  })

  it('rejects blank values', () => {
    expect(() =>
      resolveAuthConfig({ DEV: true, VITE_AUTH_AUTHORITY: '  ', VITE_AUTH_CLIENT_ID: 'x' }),
    ).toThrow('登录域未配置')
  })
})

describe('URL builders', () => {
  it('builds an authorization-code URL with PKCE S256', () => {
    const url = new URL(
      buildAuthUrl(config, 'http://localhost:3000/', 'state-1', 'challenge-1'),
    )

    expect(`${url.origin}${url.pathname}`).toBe(
      'https://kc.example/realms/dev/protocol/openid-connect/auth',
    )
    expect(url.searchParams.get('client_id')).toBe('opensandbox-web')
    expect(url.searchParams.get('response_type')).toBe('code')
    expect(url.searchParams.get('redirect_uri')).toBe('http://localhost:3000/')
    expect(url.searchParams.get('scope')).toBe('openid profile')
    expect(url.searchParams.get('state')).toBe('state-1')
    expect(url.searchParams.get('code_challenge')).toBe('challenge-1')
    expect(url.searchParams.get('code_challenge_method')).toBe('S256')
  })

  it('builds a logout URL with the id token hint', () => {
    const url = new URL(buildLogoutUrl(config, 'http://localhost:3000/', 'id-1'))

    expect(`${url.origin}${url.pathname}`).toBe(
      'https://kc.example/realms/dev/protocol/openid-connect/logout',
    )
    expect(url.searchParams.get('client_id')).toBe('opensandbox-web')
    expect(url.searchParams.get('post_logout_redirect_uri')).toBe('http://localhost:3000/')
    expect(url.searchParams.get('id_token_hint')).toBe('id-1')
  })
})

describe('parseCallback', () => {
  it('returns code and state', () => {
    expect(parseCallback('?code=abc&state=s1')).toEqual({ code: 'abc', state: 's1' })
  })

  it('returns null when there is no callback', () => {
    expect(parseCallback('')).toBeNull()
    expect(parseCallback('?foo=bar')).toBeNull()
  })

  it('throws a typed error when Keycloak rejects the login', () => {
    expect(() => parseCallback('?error=access_denied&error_description=denied')).toThrow(
      AuthorizationRejected,
    )
  })

  it('requires both code and state', () => {
    expect(() => parseCallback('?code=abc')).toThrow('state')
    expect(() => parseCallback('?state=s1')).toThrow('code')
  })
})

describe('refresh threshold and state', () => {
  it('refreshes inside the 60 second window', () => {
    expect(needsRefresh(1_000_000, 1_000_000 - 59_999)).toBe(true)
    expect(needsRefresh(1_000_000, 1_000_000 - 60_000)).toBe(false)
  })

  it('creates a 22-character random state', () => {
    const first = randomState()
    const second = randomState()
    expect(first).toMatch(/^[A-Za-z0-9_-]{22}$/)
    expect(second).not.toBe(first)
  })
})
```

- [ ] **Step 7: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/authModel.test.ts`

Expected: 失败，提示无法解析 `../authModel`。

- [ ] **Step 8: 实现认证模型**

新建 `frontend/src/auth/authModel.ts`：

```ts
import { base64Url } from './pkce'

export type AuthConfig = { authority: string; clientId: string }

type InjectedAuth = { authority?: string; clientId?: string }
type RuntimeEnv = {
  DEV?: boolean
  VITE_AUTH_AUTHORITY?: string
  VITE_AUTH_CLIENT_ID?: string
}

const MISSING_CONFIG_MESSAGE =
  '登录域未配置：后端需在 index.html 注入 __OPENSANDBOX_AUTH__'

export function readInjectedAuth(): InjectedAuth | undefined {
  return (globalThis as { __OPENSANDBOX_AUTH__?: InjectedAuth }).__OPENSANDBOX_AUTH__
}

export function resolveAuthConfig(env: RuntimeEnv = import.meta.env): AuthConfig {
  const injected = readInjectedAuth()
  const buildTime = env.DEV
    ? {
        authority: env.VITE_AUTH_AUTHORITY,
        clientId: env.VITE_AUTH_CLIENT_ID,
      }
    : { authority: undefined, clientId: undefined }

  const authority = (injected?.authority?.trim() || buildTime.authority?.trim() || '')
    .replace(/\/+$/, '')
  const clientId = injected?.clientId?.trim() || buildTime.clientId?.trim() || ''

  if (!authority || !clientId) throw new Error(MISSING_CONFIG_MESSAGE)
  return { authority, clientId }
}

export function oidcBase(config: AuthConfig): string {
  return `${config.authority}/protocol/openid-connect`
}

export function tokenEndpoint(config: AuthConfig): string {
  return `${oidcBase(config)}/token`
}

export function buildAuthUrl(
  config: AuthConfig,
  redirectUri: string,
  state: string,
  codeChallenge: string,
): string {
  const query = new URLSearchParams({
    client_id: config.clientId,
    redirect_uri: redirectUri,
    response_type: 'code',
    scope: 'openid profile',
    state,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
  })
  return `${oidcBase(config)}/auth?${query.toString()}`
}

export function buildLogoutUrl(
  config: AuthConfig,
  redirectUri: string,
  idToken: string,
): string {
  const query = new URLSearchParams({
    client_id: config.clientId,
    post_logout_redirect_uri: redirectUri,
  })
  if (idToken) query.set('id_token_hint', idToken)
  return `${oidcBase(config)}/logout?${query.toString()}`
}

export class AuthorizationRejected extends Error {
  readonly code: string
  readonly description?: string

  constructor(code: string, description?: string) {
    super(description ? `登录被拒绝：${code}（${description}）` : `登录被拒绝：${code}`)
    this.name = 'AuthorizationRejected'
    this.code = code
    this.description = description
  }
}

export function parseCallback(search: string): { code: string; state: string } | null {
  const params = new URLSearchParams(search)
  const error = params.get('error')
  if (error) {
    throw new AuthorizationRejected(error, params.get('error_description') ?? undefined)
  }

  const code = params.get('code')
  const state = params.get('state')
  if (!code && !state) return null
  if (!code) throw new Error('登录回调缺少 code')
  if (!state) throw new Error('登录回调缺少 state')
  return { code, state }
}

export function needsRefresh(expiresAtMs: number, nowMs: number): boolean {
  return expiresAtMs - nowMs < 60_000
}

export function randomState(): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(16)))
}
```

- [ ] **Step 9: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/pkce.test.ts src/auth/__tests__/authModel.test.ts`

Expected: 全部通过。

- [ ] **Step 10: 提交**

```bash
git add frontend/package.json frontend/package-lock.json frontend/src/auth/pkce.ts frontend/src/auth/authModel.ts frontend/src/auth/__tests__/pkce.test.ts frontend/src/auth/__tests__/authModel.test.ts
git commit -m "feat(auth): add frontend PKCE and auth model" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 前端会话、换票、续签与请求认证

**Files:**

- Create: `frontend/src/auth/auth.ts`
- Create: `frontend/src/auth/__tests__/auth.test.ts`
- Modify: `frontend/src/api/client.ts`
- Modify: `frontend/src/hooks/useMetricsStream.ts`

**Interfaces:**

- Consumes: Task 4 的 `AuthConfig`、`buildAuthUrl`、`buildLogoutUrl`、`parseCallback`、`needsRefresh`、`randomState`、`resolveAuthConfig`、`tokenEndpoint`、`createPkce`
- Produces: `STORAGE_KEYS`、`currentAccessToken(now?)`、`login(options?)`、`logout()`、`requireLogin()`、`getToken()`、`refreshSession()`、`ensureAuthenticated()`、`ensureAuthenticatedOnce()`、`completeLoginCallback(callback)`、`clearSession()`、`setAuthNavigatorForTests(fn)`、`resetAuthNavigatorForTests()`

- [ ] **Step 1: 写会话失败测试**

新建 `frontend/src/auth/__tests__/auth.test.ts`：

```ts
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  STORAGE_KEYS,
  clearSession,
  completeLoginCallback,
  ensureAuthenticated,
  getToken,
  login,
  resetAuthNavigatorForTests,
  setAuthNavigatorForTests,
} from '../auth'
import { challengeFor } from '../pkce'
import { AuthorizationRejected } from '../authModel'

type Injected = { authority?: string; clientId?: string }

let navigated = ''

beforeEach(() => {
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
  ;(globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__ = {
    authority: 'https://kc.example/realms/dev',
    clientId: 'opensandbox-web',
  }
  navigated = ''
  setAuthNavigatorForTests((url) => {
    navigated = url
  })
})

afterEach(() => {
  resetAuthNavigatorForTests()
  vi.unstubAllGlobals()
  delete (globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
})

describe('login', () => {
  it('stores a PKCE transaction and redirects to Keycloak', () => {
    login({ returnTo: '/sandboxes' })

    const url = new URL(navigated)
    expect(url.pathname).toBe('/realms/dev/protocol/openid-connect/auth')
    const state = url.searchParams.get('state')!
    expect(state).toMatch(/^[A-Za-z0-9_-]{22}$/)

    const stored = JSON.parse(sessionStorage.getItem(`osb_login_${state}`)!)
    expect(stored.returnTo).toBe('/sandboxes')
    expect(stored.exchangeStarted).toBe(false)
    expect(stored.codeVerifier).toMatch(/^[A-Za-z0-9_-]{43}$/)
    expect(url.searchParams.get('code_challenge')).toBe(challengeFor(stored.codeVerifier))
  })

  it('strips stale OIDC parameters from returnTo', () => {
    window.history.replaceState(null, '', '/sandboxes?code=old&state=old&tab=1')

    login()

    const state = new URL(navigated).searchParams.get('state')!
    const stored = JSON.parse(sessionStorage.getItem(`osb_login_${state}`)!)
    expect(stored.returnTo).toBe('/sandboxes?tab=1')
  })
})

describe('authorization code exchange', () => {
  it('exchanges the code, stores tokens, and restores returnTo', async () => {
    const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/sandboxes',
        createdAt: Date.now(),
        exchangeStarted: false,
        codeVerifier: verifier,
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        access_token: 'at',
        expires_in: 300,
        refresh_token: 'rt',
        id_token: 'it',
      }),
    })
    vi.stubGlobal('fetch', fetchMock)

    await ensureAuthenticated()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('https://kc.example/realms/dev/protocol/openid-connect/token')
    const body = (init as { body: URLSearchParams }).body
    expect(body.get('grant_type')).toBe('authorization_code')
    expect(body.get('code')).toBe('abc')
    expect(body.get('code_verifier')).toBe(verifier)
    expect(body.get('redirect_uri')).toBe(`${window.location.origin}/`)
    expect(sessionStorage.getItem(STORAGE_KEYS.access)).toBe('at')
    expect(sessionStorage.getItem(STORAGE_KEYS.refresh)).toBe('rt')
    expect(sessionStorage.getItem(STORAGE_KEYS.idToken)).toBe('it')
    expect(sessionStorage.getItem('osb_login_s1')).toBeNull()
    expect(window.location.pathname).toBe('/sandboxes')
  })

  it('rejects a replayed transaction', async () => {
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/',
        createdAt: Date.now(),
        exchangeStarted: true,
        codeVerifier: 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')

    await expect(ensureAuthenticated()).rejects.toThrow('已经处理过')
  })

  it('rejects an expired transaction', async () => {
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/',
        createdAt: Date.now() - 16 * 60 * 1000,
        exchangeStarted: false,
        codeVerifier: 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')

    await expect(ensureAuthenticated()).rejects.toThrow('15 分钟')
  })

  it('rejects an unknown state', async () => {
    window.history.replaceState(null, '', '/?code=abc&state=missing')

    await expect(ensureAuthenticated()).rejects.toThrow('没有这次登录记录')
  })

  it('surfaces Keycloak callback errors', async () => {
    window.history.replaceState(null, '', '/?error=access_denied&error_description=denied')

    await expect(ensureAuthenticated()).rejects.toBeInstanceOf(AuthorizationRejected)
  })
})

describe('token refresh', () => {
  it('uses a single in-flight refresh for concurrent callers', async () => {
    let resolveResponse!: (value: unknown) => void
    const fetchMock = vi.fn(
      () =>
        new Promise((resolve) => {
          resolveResponse = resolve
        }),
    )
    vi.stubGlobal('fetch', fetchMock)
    sessionStorage.setItem(STORAGE_KEYS.refresh, 'rt')
    sessionStorage.setItem(STORAGE_KEYS.expiresAt, String(Date.now() - 1))

    const first = getToken()
    const second = getToken()

    expect(fetchMock).toHaveBeenCalledTimes(1)

    resolveResponse({
      ok: true,
      json: async () => ({ access_token: 'at2', expires_in: 300 }),
    })

    await expect(Promise.all([first, second])).resolves.toEqual(['at2', 'at2'])
    const body = (fetchMock.mock.calls[0][1] as { body: URLSearchParams }).body
    expect(body.get('grant_type')).toBe('refresh_token')
    expect(body.get('refresh_token')).toBe('rt')
  })

  it('starts a new login when there is no refresh token', async () => {
    const token = getToken()

    expect(navigated).toContain('/protocol/openid-connect/auth?')
    expect(await Promise.race([token, Promise.resolve('pending')])).toBe('pending')
    expect(clearSession).toBeTypeOf('function')
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/auth.test.ts`

Expected: 失败，提示无法解析 `../auth`。

- [ ] **Step 3: 实现会话逻辑**

新建 `frontend/src/auth/auth.ts`：

```ts
import {
  buildAuthUrl,
  buildLogoutUrl,
  needsRefresh,
  parseCallback,
  randomState,
  resolveAuthConfig,
  tokenEndpoint,
  type AuthConfig,
} from './authModel'
import { createPkce } from './pkce'

export const STORAGE_KEYS = {
  access: 'osb_at',
  refresh: 'osb_rt',
  idToken: 'osb_it',
  expiresAt: 'osb_exp',
} as const

const TRANSACTION_PREFIX = 'osb_login_'
const TRANSACTION_TTL_MS = 15 * 60 * 1000
const CALLBACK_PARAMS = ['code', 'state', 'error', 'error_description', 'session_state', 'iss']

type LoginTransaction = {
  returnTo: string
  createdAt: number
  exchangeStarted: boolean
  codeVerifier: string
}

type TokenResponse = {
  access_token?: string
  expires_in?: number
  refresh_token?: string
  id_token?: string
}

let navigate: (url: string) => void = (url) => window.location.assign(url)

export function setAuthNavigatorForTests(fn: (url: string) => void): void {
  navigate = fn
}

export function resetAuthNavigatorForTests(): void {
  navigate = (url) => window.location.assign(url)
}

export function clearSession(): void {
  for (const key of Object.values(STORAGE_KEYS)) sessionStorage.removeItem(key)
}

export function currentAccessToken(now = Date.now()): string | null {
  const token = sessionStorage.getItem(STORAGE_KEYS.access)
  const expiresAt = Number(sessionStorage.getItem(STORAGE_KEYS.expiresAt) ?? 0)
  if (!token || !expiresAt || now >= expiresAt) return null
  return token
}

export function login(options: { returnTo?: string } = {}): void {
  const config = resolveAuthConfig()
  const state = randomState()
  const pkce = createPkce()
  pruneTransactions()

  const transaction: LoginTransaction = {
    returnTo: options.returnTo ?? currentReturnTo(),
    createdAt: Date.now(),
    exchangeStarted: false,
    codeVerifier: pkce.verifier,
  }
  sessionStorage.setItem(TRANSACTION_PREFIX + state, JSON.stringify(transaction))
  navigate(buildAuthUrl(config, redirectUri(), state, pkce.challenge))
}

export function logout(): void {
  const config = resolveAuthConfig()
  const idToken = sessionStorage.getItem(STORAGE_KEYS.idToken) ?? ''
  clearSession()
  navigate(buildLogoutUrl(config, redirectUri(), idToken))
}

export function requireLogin(): Promise<never> {
  clearSession()
  login()
  return new Promise<never>(() => {})
}

export async function getToken(): Promise<string> {
  const access = sessionStorage.getItem(STORAGE_KEYS.access)
  const expiresAt = Number(sessionStorage.getItem(STORAGE_KEYS.expiresAt) ?? 0)
  const now = Date.now()

  if (access && expiresAt > now && !needsRefresh(expiresAt, now)) return access

  if (sessionStorage.getItem(STORAGE_KEYS.refresh)) {
    try {
      await refreshSession()
      const refreshed = sessionStorage.getItem(STORAGE_KEYS.access)
      if (refreshed) return refreshed
    } catch {
      // 续签失败按未登录处理
    }
  }

  return requireLogin()
}

let refreshInFlight: Promise<void> | null = null

export function refreshSession(): Promise<void> {
  if (refreshInFlight) return refreshInFlight

  const refreshToken = sessionStorage.getItem(STORAGE_KEYS.refresh)
  if (!refreshToken) return Promise.reject(new Error('当前登录缺少续签凭据'))

  const request = exchange(resolveAuthConfig(), {
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
  })
  refreshInFlight = request
  void request
    .finally(() => {
      if (refreshInFlight === request) refreshInFlight = null
    })
    .catch(() => {})

  return request
}

export async function ensureAuthenticated(): Promise<void> {
  const callback = parseCallback(window.location.search)
  if (callback) {
    await completeLoginCallback(callback)
    return
  }

  if (currentAccessToken()) return
  return requireLogin()
}

let bootPromise: Promise<void> | null = null

export function ensureAuthenticatedOnce(): Promise<void> {
  bootPromise ??= ensureAuthenticated()
  return bootPromise
}

export async function completeLoginCallback(callback: {
  code: string
  state: string
}): Promise<void> {
  const key = TRANSACTION_PREFIX + callback.state
  const raw = sessionStorage.getItem(key)
  if (!raw) throw new Error('登录回调对不上：当前标签页没有这次登录记录，请重新登录')

  let transaction: LoginTransaction
  try {
    transaction = JSON.parse(raw) as LoginTransaction
  } catch {
    sessionStorage.removeItem(key)
    throw new Error('登录回调对不上：请重新登录')
  }

  if (Date.now() - transaction.createdAt >= TRANSACTION_TTL_MS) {
    sessionStorage.removeItem(key)
    throw new Error('登录超过 15 分钟没有完成，请重新登录')
  }
  if (transaction.exchangeStarted) {
    throw new Error('这次登录已经处理过，请重新登录')
  }
  if (!/^[A-Za-z0-9_-]{43}$/.test(transaction.codeVerifier)) {
    sessionStorage.removeItem(key)
    throw new Error('登录回调对不上：请重新登录')
  }

  sessionStorage.setItem(key, JSON.stringify({ ...transaction, exchangeStarted: true }))
  await exchange(resolveAuthConfig(), {
    grant_type: 'authorization_code',
    code: callback.code,
    redirect_uri: redirectUri(),
    code_verifier: transaction.codeVerifier,
  })
  sessionStorage.removeItem(key)
  window.history.replaceState(null, '', transaction.returnTo || '/')
}

async function exchange(config: AuthConfig, params: Record<string, string>): Promise<void> {
  const response = await fetch(tokenEndpoint(config), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: config.clientId, ...params }),
  })
  if (!response.ok) throw new Error(`登录令牌请求失败（HTTP ${response.status}）`)

  const body = (await response.json()) as TokenResponse
  if (!body.access_token) throw new Error('登录响应缺少 access_token')

  sessionStorage.setItem(STORAGE_KEYS.access, body.access_token)
  sessionStorage.setItem(
    STORAGE_KEYS.expiresAt,
    String(Date.now() + (body.expires_in ?? 300) * 1000),
  )
  if (body.refresh_token) sessionStorage.setItem(STORAGE_KEYS.refresh, body.refresh_token)
  if (body.id_token) sessionStorage.setItem(STORAGE_KEYS.idToken, body.id_token)
}

function currentReturnTo(): string {
  const url = new URL(window.location.href)
  for (const key of CALLBACK_PARAMS) url.searchParams.delete(key)
  const search = url.searchParams.toString()
  return `${url.pathname}${search ? `?${search}` : ''}`
}

function redirectUri(): string {
  return `${window.location.origin}/`
}

function pruneTransactions(): void {
  const now = Date.now()
  for (let index = sessionStorage.length - 1; index >= 0; index -= 1) {
    const key = sessionStorage.key(index)
    if (!key?.startsWith(TRANSACTION_PREFIX)) continue

    try {
      const transaction = JSON.parse(sessionStorage.getItem(key) ?? '') as LoginTransaction
      if (now - transaction.createdAt >= TRANSACTION_TTL_MS) sessionStorage.removeItem(key)
    } catch {
      sessionStorage.removeItem(key)
    }
  }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/auth.test.ts`

Expected: 全部通过。

- [ ] **Step 5: 给 axios 与 SSE 注入 Bearer，401 自动重新登录**

把 `frontend/src/api/client.ts` 整体替换为：

```ts
import axios from 'axios'
import { getToken, requireLogin } from '@/auth/auth'

// 与后端 SDK 默认 RequestTimeoutSeconds 一致；创建请求单独放宽（见 sandbox.ts）。
export const apiClient = axios.create({
  baseURL: '/api/v1',
  timeout: 30000,
})

apiClient.interceptors.request.use(async (config) => {
  const token = await getToken()
  config.headers.set('Authorization', `Bearer ${token}`)
  return config
})

apiClient.interceptors.response.use(
  (response) => response,
  async (error: unknown) => {
    if (axios.isAxiosError(error) && error.response?.status === 401) {
      await requireLogin()
    }
    return Promise.reject(error)
  },
)

export function extractErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error) && error.response?.data) {
    const data = error.response.data as { message?: string }
    if (data.message) return data.message
  }
  return error instanceof Error ? error.message : 'Unknown error'
}
```

修改 `frontend/src/hooks/useMetricsStream.ts`：在文件顶部加入 import：

```ts
import { getToken, requireLogin } from '@/auth/auth'
```

把 `fetch` 调用改为携带 Bearer，并在 401 时重新登录：

```ts
      try {
        const token = await getToken()
        const response = await fetch(`/api/v1/sandboxes/${sandboxId}/metrics/watch`, {
          signal: controller.signal,
          headers: { Authorization: `Bearer ${token}` },
        })
        if (response.status === 401) {
          await requireLogin()
          return
        }
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`)
```

- [ ] **Step 6: 运行前端测试与构建**

Run（在 `frontend/`）:

```bash
npm test
npm run lint
npm run build
```

Expected: 测试、lint、TypeScript 构建全部通过。

- [ ] **Step 7: 提交**

```bash
git add frontend/src/auth/auth.ts frontend/src/auth/__tests__/auth.test.ts frontend/src/api/client.ts frontend/src/hooks/useMetricsStream.ts
git commit -m "feat(auth): add frontend session and authenticated requests" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: 启动门控、身份上下文与角色化界面

**Files:**

- Create: `frontend/src/auth/roles.ts`
- Create: `frontend/src/auth/__tests__/roles.test.ts`
- Create: `frontend/src/api/console.ts`
- Create: `frontend/src/auth/AuthContext.tsx`
- Create: `frontend/src/auth/AuthSplash.tsx`
- Create: `frontend/src/auth/AuthFailure.tsx`
- Create: `frontend/src/auth/NoPermission.tsx`
- Create: `frontend/src/auth/RequireSandboxAdmin.tsx`
- Modify: `frontend/src/main.tsx`
- Modify: `frontend/src/router/index.tsx`
- Modify: `frontend/src/components/layout/AppLayout.tsx`
- Modify: `frontend/src/pages/Settings.tsx`
- Modify: `frontend/src/pages/SandboxList.tsx`
- Modify: `frontend/src/pages/SandboxDetail.tsx`
- Modify: `frontend/src/components/layout/__tests__/AppLayout.test.tsx`
- Modify: `frontend/src/pages/__tests__/SandboxList.test.tsx`

**Interfaces:**

- Consumes: `auth.ts`、`consoleApi`、`hasConsoleRole`
- Produces: `useAuth(): { me: ConsoleMe; can(role: string): boolean }`、`AuthProvider`、`RequireSandboxAdmin`、`consoleApi.me(): Promise<ConsoleMe>`、`hasConsoleRole(roles, required)`

- [ ] **Step 1: 写角色失败测试**

新建 `frontend/src/auth/__tests__/roles.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { hasConsoleRole } from '../roles'

describe('hasConsoleRole', () => {
  it('lets admin satisfy viewer in the same facet', () => {
    expect(hasConsoleRole(['sandbox:admin'], 'sandbox:viewer')).toBe(true)
    expect(hasConsoleRole(['volume:admin'], 'volume:viewer')).toBe(true)
  })

  it('does not let viewer satisfy admin', () => {
    expect(hasConsoleRole(['sandbox:viewer'], 'sandbox:admin')).toBe(false)
    expect(hasConsoleRole(['volume:viewer'], 'volume:admin')).toBe(false)
  })

  it('keeps facets independent', () => {
    expect(hasConsoleRole(['sandbox:admin'], 'volume:viewer')).toBe(false)
    expect(hasConsoleRole(['volume:admin'], 'sandbox:viewer')).toBe(false)
  })

  it('matches exact roles and denies empty sets', () => {
    expect(hasConsoleRole(['sandbox:viewer'], 'sandbox:viewer')).toBe(true)
    expect(hasConsoleRole([], 'sandbox:viewer')).toBe(false)
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/roles.test.ts`

Expected: 失败，提示无法解析 `../roles`。

- [ ] **Step 3: 实现前端角色判定**

新建 `frontend/src/auth/roles.ts`：

```ts
export function hasConsoleRole(roles: readonly string[], required: string): boolean {
  if (roles.includes(required)) return true
  if (required === 'sandbox:viewer') return roles.includes('sandbox:admin')
  if (required === 'volume:viewer') return roles.includes('volume:admin')
  return false
}
```

- [ ] **Step 4: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/roles.test.ts`

Expected: 全部通过。

- [ ] **Step 5: 实现身份 API、上下文、启动/失败/无权限界面与路由守卫**

新建 `frontend/src/api/console.ts`：

```ts
import { apiClient } from './client'

export type ConsoleMe = {
  subject: string
  username: string
  roles: string[]
}

export const consoleApi = {
  async me(): Promise<ConsoleMe> {
    const response = await apiClient.get<ConsoleMe>('/console/me')
    return response.data
  },
}
```

新建 `frontend/src/auth/AuthSplash.tsx`：

```tsx
import { Spin } from 'antd'

export function FullPageSpin() {
  return (
    <div style={{ minHeight: '100vh', display: 'grid', placeItems: 'center' }}>
      <Spin size="large" />
    </div>
  )
}
```

新建 `frontend/src/auth/AuthFailure.tsx`：

```tsx
import { Button, Result, Space } from 'antd'
import { extractErrorMessage } from '@/api/client'
import { login, logout } from './auth'

export default function AuthFailure({ error }: { error: unknown }) {
  return (
    <Result
      status="error"
      title="无法完成登录"
      subTitle={extractErrorMessage(error)}
      extra={
        <Space>
          <Button type="primary" onClick={() => login()}>
            重新登录
          </Button>
          <Button onClick={logout}>注销</Button>
        </Space>
      }
    />
  )
}
```

新建 `frontend/src/auth/NoPermission.tsx`：

```tsx
import { Button, Result, Space } from 'antd'
import type { ConsoleMe } from '@/api/console'
import { logout } from './auth'

export default function NoPermission({ me }: { me: ConsoleMe }) {
  const hasVolumeRole =
    me.roles.includes('volume:admin') || me.roles.includes('volume:viewer')

  return (
    <Result
      status="403"
      title="没有控制台访问权限"
      subTitle={
        hasVolumeRole
          ? `账号 ${me.username} 只有卷角色；卷管理界面将在后续版本提供。`
          : `账号 ${me.username} 没有 sandbox:viewer 或 sandbox:admin 角色，请联系管理员。`
      }
      extra={
        <Space>
          <Button onClick={logout}>注销</Button>
        </Space>
      }
    />
  )
}
```

新建 `frontend/src/auth/AuthContext.tsx`：

```tsx
import { createContext, useContext, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { consoleApi, type ConsoleMe } from '@/api/console'
import { FullPageSpin } from './AuthSplash'
import AuthFailure from './AuthFailure'
import NoPermission from './NoPermission'
import { hasConsoleRole } from './roles'

type AuthContextValue = {
  me: ConsoleMe
  can: (role: string) => boolean
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['console', 'me'],
    queryFn: consoleApi.me,
    retry: false,
    staleTime: 5 * 60 * 1000,
  })

  if (isLoading) return <FullPageSpin />
  if (isError || !data) return <AuthFailure error={error} />

  const hasSandboxRole =
    data.roles.includes('sandbox:admin') || data.roles.includes('sandbox:viewer')
  if (!hasSandboxRole) return <NoPermission me={data} />

  return (
    <AuthContext.Provider
      value={{ me: data, can: (role) => hasConsoleRole(data.roles, role) }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth 必须在 AuthProvider 内使用')
  return context
}
```

新建 `frontend/src/auth/RequireSandboxAdmin.tsx`：

```tsx
import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function RequireSandboxAdmin({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  if (!can('sandbox:admin')) return <Navigate to="/dashboard" replace />
  return <>{children}</>
}
```

- [ ] **Step 6: 接入启动门控、路由守卫和菜单**

把 `frontend/src/main.tsx` 整体替换为：

```tsx
import React, { useEffect, useState, type ReactNode } from 'react'
import ReactDOM from 'react-dom/client'
import { RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { ensureAuthenticatedOnce } from '@/auth/auth'
import { AuthProvider } from '@/auth/AuthContext'
import { FullPageSpin } from '@/auth/AuthSplash'
import AuthFailure from '@/auth/AuthFailure'
import { router } from '@/router'
import 'antd/dist/reset.css'
import '@/styles/global.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 5000 } },
})

function BootGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<'checking' | 'ready' | 'failed'>('checking')
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let active = true
    ensureAuthenticatedOnce()
      .then(() => {
        if (active) setPhase('ready')
      })
      .catch((err: unknown) => {
        if (!active) return
        setError(err)
        setPhase('failed')
      })
    return () => {
      active = false
    }
  }, [])

  if (phase === 'failed') return <AuthFailure error={error} />
  if (phase === 'checking') return <FullPageSpin />
  return <>{children}</>
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider locale={zhCN}>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <BootGate>
            <AuthProvider>
              <RouterProvider router={router} />
            </AuthProvider>
          </BootGate>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  </React.StrictMode>,
)
```

修改 `frontend/src/router/index.tsx`：加入 import 并包住两个写页面：

```tsx
import { RequireSandboxAdmin } from '@/auth/RequireSandboxAdmin'
```

```tsx
      {
        path: 'create',
        element: (
          <RequireSandboxAdmin>
            <CreateSandbox />
          </RequireSandboxAdmin>
        ),
      },
      {
        path: 'batch-create',
        element: (
          <RequireSandboxAdmin>
            <BatchCreate />
          </RequireSandboxAdmin>
        ),
      },
```

把 `frontend/src/components/layout/AppLayout.tsx` 整体替换为：

```tsx
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Avatar, Dropdown, Layout, Menu, Space, Typography, theme } from 'antd'
import type { MenuProps } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  LogoutOutlined,
  PlusOutlined,
  SettingOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { logout } from '@/auth/auth'
import { useAuth } from '@/auth/AuthContext'

const { Sider, Header, Content } = Layout

const MENU_ITEMS = [
  { key: '/dashboard', icon: <AppstoreOutlined />, label: '仪表盘', role: 'sandbox:viewer' },
  { key: '/sandboxes', icon: <BarsOutlined />, label: '沙箱列表', role: 'sandbox:viewer' },
  { key: '/create', icon: <PlusOutlined />, label: '创建沙箱', role: 'sandbox:admin' },
  {
    key: '/batch-create',
    icon: <ThunderboltOutlined />,
    label: '批量创建',
    role: 'sandbox:admin',
  },
  { key: '/settings', icon: <SettingOutlined />, label: '设置', role: null },
] as const

export default function AppLayout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { token } = theme.useToken()
  const { me, can } = useAuth()

  const menuItems = MENU_ITEMS.filter((item) => !item.role || can(item.role)).map(
    ({ key, icon, label }) => ({ key, icon, label }),
  )

  const selectedKey =
    menuItems.find((item) => location.pathname.startsWith(item.key))?.key ??
    menuItems[0]?.key ??
    '/settings'

  const userMenu: MenuProps['items'] = [
    {
      key: 'account',
      disabled: true,
      label: `${me.username}（${me.roles.join('、') || '无角色'}）`,
    },
    { type: 'divider' },
    { key: 'logout', icon: <LogoutOutlined />, label: '注销', onClick: logout },
  ]

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
          items={menuItems}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            background: token.colorBgContainer,
            padding: '0 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <Typography.Title level={4} style={{ margin: 0 }}>
            OpenSandbox 管理控制台
          </Typography.Title>
          <Dropdown menu={{ items: userMenu }}>
            <Space style={{ cursor: 'pointer' }}>
              <Avatar size="small" icon={<UserOutlined />} />
              <Typography.Text>{me.username}</Typography.Text>
            </Space>
          </Dropdown>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
```

- [ ] **Step 7: 门控沙箱写操作与设置页账号信息**

修改 `frontend/src/pages/SandboxList.tsx`：

在 import 区加入：

```tsx
import { useAuth } from '@/auth/AuthContext'
```

在组件内、`const [selected, ...]` 之后加入：

```tsx
  const { can } = useAuth()
  const canWrite = can('sandbox:admin')
```

把操作列中的删除按钮改为条件渲染：

```tsx
          <Button size="small" onClick={() => navigate(`/sandboxes/${record.id}`)}>
            详情
          </Button>
          {canWrite && (
            <Button
              size="small"
              danger
              icon={<DeleteOutlined />}
              onClick={() => confirmDelete([record.id])}
            />
          )}
```

把批量删除按钮改为条件渲染：

```tsx
          {canWrite && (
            <Button
              danger
              icon={<DeleteOutlined />}
              disabled={selected.length === 0}
              onClick={() => confirmDelete(selected.map(String))}
            >
              批量删除
            </Button>
          )}
```

把 `rowSelection` 改为仅在可写时启用：

```tsx
        rowSelection={
          canWrite ? { selectedRowKeys: selected, onChange: setSelected } : undefined
        }
```

修改 `frontend/src/pages/SandboxDetail.tsx`：

在 import 区加入：

```tsx
import { useAuth } from '@/auth/AuthContext'
```

在组件内、`const isRunning = ...` 之前加入：

```tsx
  const { can } = useAuth()
  const canWrite = can('sandbox:admin')
```

把 Card 的 `extra` 整个替换为：

```tsx
        extra={
          canWrite ? (
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
          ) : null
        }
```

修改 `frontend/src/pages/Settings.tsx`：在 import 区加入：

```tsx
import { useAuth } from '@/auth/AuthContext'
import { resolveAuthConfig } from '@/auth/authModel'
```

在组件内、`changeInterval` 之后加入：

```tsx
  const { me } = useAuth()
  let authConfig: { authority: string; clientId: string } | null = null
  try {
    authConfig = resolveAuthConfig()
  } catch {
    authConfig = null
  }
```

在“API 端点配置”的 `<Divider />` 之后插入账号区块：

```tsx
      <Typography.Title level={5}>当前账号</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="用户名">{me.username}</Descriptions.Item>
        <Descriptions.Item label="Subject">{me.subject}</Descriptions.Item>
        <Descriptions.Item label="角色">{me.roles.join('、') || '无'}</Descriptions.Item>
        <Descriptions.Item label="Keycloak Realm">
          {authConfig?.authority ?? '未配置'}
        </Descriptions.Item>
        <Descriptions.Item label="客户端 ID">
          {authConfig?.clientId ?? '未配置'}
        </Descriptions.Item>
      </Descriptions>

      <Divider />
```

- [ ] **Step 8: 更新受 `useAuth` 影响的现有测试**

在 `frontend/src/components/layout/__tests__/AppLayout.test.tsx` 的 import 之后加入：

```ts
vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test-admin', username: 'tester', roles: ['sandbox:admin'] },
    can: () => true,
  }),
}))
```

并把文件第一行的 import 改为：

```ts
import { describe, expect, it, vi } from 'vitest'
```

在 `frontend/src/pages/__tests__/SandboxList.test.tsx` 的 import 之后加入同样的 mock：

```ts
vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test-admin', username: 'tester', roles: ['sandbox:admin'] },
    can: () => true,
  }),
}))
```

- [ ] **Step 9: 运行前端测试、lint 与构建**

Run（在 `frontend/`）:

```bash
npm test
npm run lint
npm run build
```

Expected: 全部通过。若 TypeScript 报 `useAuth` 缺少 provider 或未使用变量，按错误修到通过，不要放宽 `noUnusedLocals`。

- [ ] **Step 10: 提交**

```bash
git add frontend/src/auth/roles.ts frontend/src/auth/__tests__/roles.test.ts frontend/src/api/console.ts frontend/src/auth/AuthContext.tsx frontend/src/auth/AuthSplash.tsx frontend/src/auth/AuthFailure.tsx frontend/src/auth/NoPermission.tsx frontend/src/auth/RequireSandboxAdmin.tsx frontend/src/main.tsx frontend/src/router/index.tsx frontend/src/components/layout/AppLayout.tsx frontend/src/pages/Settings.tsx frontend/src/pages/SandboxList.tsx frontend/src/pages/SandboxDetail.tsx frontend/src/components/layout/__tests__/AppLayout.test.tsx frontend/src/pages/__tests__/SandboxList.test.tsx
git commit -m "feat(auth): gate the console by Keycloak roles" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 7: 部署配置、开发环境与文档

**Files:**

- Modify: `docker-compose.yml`
- Modify: `.env.example`
- Create: `frontend/.env.example`
- Modify: `README.md`

**Interfaces:**

- Consumes: Task 1 的 `Auth` 配置键
- Produces: 容器与 Vite 开发环境所需的完整配置入口

- [ ] **Step 1: 补容器与开发环境配置**

修改 `docker-compose.yml` 的 `opensandbox-web.environment`，加入：

```yaml
      Auth__Authority: ${AUTH_AUTHORITY:?AUTH_AUTHORITY is required}
      Auth__ClientId: ${AUTH_CLIENT_ID:-opensandbox-web}
      Auth__RequireHttpsMetadata: ${AUTH_REQUIRE_HTTPS_METADATA:-true}
```

在 `.env.example` 末尾追加：

```bash
# 控制台 Keycloak 登录：realm 地址与公共客户端 ID（必填）
AUTH_AUTHORITY=https://dev-passport.ai4c.cn/realms/ai4cbrain
AUTH_CLIENT_ID=opensandbox-web
# Keycloak 使用 HTTP（本地/内网）时设为 false；HTTPS 保持 true
AUTH_REQUIRE_HTTPS_METADATA=true
```

新建 `frontend/.env.example`：

```bash
# 仅 Vite 开发服务器使用；生产由后端运行时注入 __OPENSANDBOX_AUTH__
VITE_AUTH_AUTHORITY=https://dev-passport.ai4c.cn/realms/ai4cbrain
VITE_AUTH_CLIENT_ID=opensandbox-web
```

- [ ] **Step 2: 更新 README 英文部分**

在 English 的 Features 列表末尾加入：

```markdown
- **Keycloak SSO** - Console login through Keycloak with separate sandbox and volume admin/viewer roles
```

把 English 的 Getting Started 代码块替换为：

```bash
# Terminal 1 - backend (http://localhost:5080)
cd backend
dotnet run

# Terminal 2 - frontend dev server (http://localhost:3000, proxies /api to 5080)
cd frontend
npm install
cp .env.example .env.local   # Keycloak realm/client for the Vite dev server
npm run dev
```

在 English 的 Configuration 段落末尾（Docker 环境变量说明之后）加入：

````markdown
Console authentication uses the `Auth` section and is required:

```json
{
  "Auth": {
    "Authority": "https://keycloak.example.com/realms/your-realm",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  }
}
```

The backend refuses to start when `Auth:Authority` or `Auth:ClientId` is missing. The Keycloak client is public, uses Standard Flow with PKCE S256, and must register the console origin (for example `http://localhost:3000/*` in development and `http://<console-host>:5080/*` in Docker) as a redirect URI and web origin.
````

在 English 的 API 表格 `/health` 行之后加入：

```markdown
| `/console/me` | GET | Current console identity and roles |
```

把 English 的 Docker 最后一条说明（`Port 5080 is published... no authentication...`）替换为：

```markdown
- The console now requires a Keycloak login. Register the console redirect URIs and web origins in the `opensandbox-web` client before exposing port 5080; the API rejects anonymous requests even if the UI is bypassed.
```

- [ ] **Step 3: 更新 README 中文部分**

在中文 Features 列表末尾加入：

```markdown
- **Keycloak 单点登录** - 控制台通过 Keycloak 登录，沙箱与卷各自区分 admin/viewer 角色
```

把中文“快速开始”代码块替换为：

```bash
# 终端 1 - 后端（http://localhost:5080）
cd backend
dotnet run

# 终端 2 - 前端开发服务器（http://localhost:3000，/api 代理到 5080）
cd frontend
npm install
cp .env.example .env.local   # Vite 开发服务器使用的 Keycloak realm/client
npm run dev
```

在中文“配置”段落末尾加入：

````markdown
控制台登录使用 `Auth` 配置，且为必填：

```json
{
  "Auth": {
    "Authority": "https://keycloak.example.com/realms/your-realm",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  }
}
```

缺少 `Auth:Authority` 或 `Auth:ClientId` 时后端拒绝启动。Keycloak 客户端为公共客户端，启用 Standard Flow + PKCE S256，并需把控制台源（开发环境如 `http://localhost:3000/*`，Docker 环境如 `http://<控制台主机>:5080/*`）登记为 redirect URI 与 web origin。
````

在中文 API 表格 `/health` 行之后加入：

```markdown
| `/console/me` | GET | 当前控制台身份与角色 |
```

把中文 Docker 最后一条（`5080 默认发布到所有网卡且管理界面无鉴权...`）替换为：

```markdown
- 控制台现在要求 Keycloak 登录。对外开放 5080 前，先在 `opensandbox-web` 客户端登记控制台 redirect URI 与 web origin；即使绕过界面，API 也会拒绝匿名请求。
```

- [ ] **Step 4: 校验 compose 与全量测试**

Run（在仓库根目录）:

```bash
docker compose --env-file .env.example config --quiet
dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj
cd frontend
npm test
npm run lint
npm run build
```

Expected: compose 配置校验通过；后端与前端测试、lint、构建全部通过。

- [ ] **Step 5: 提交**

```bash
git add docker-compose.yml .env.example frontend/.env.example README.md
git commit -m "docs(auth): document console Keycloak configuration" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## 验收清单（对应 spec FR-13 / §6.4 / §6.10–§6.12）

- [ ] 未带 token 请求 `/api/v1/sandboxes` 返回 401 且 body 为 `{code:"unauthorized"}`。
- [ ] `sandbox:viewer` 可读列表/详情/指标，但不能删除、暂停、续期、创建，也不能获取数据面 endpoint。
- [ ] `sandbox:admin` 可执行全部沙箱操作。
- [ ] `volume:viewer`/`volume:admin` 对沙箱 API 一律 403；两个分面互不隐含。
- [ ] 无角色账号可登录，`/console/me` 返回空 roles。
- [ ] `/api/v1/health` 匿名 200；`/api/v1/tenant/*` 被控制台 RBAC 跳过（留给卷治理 Key 中间件）。
- [ ] 未登记的 `/api/*` 已登录 403、匿名 401。
- [ ] `azp` 不等于 `Auth:ClientId` 的 token 被拒绝；角色只来自 `resource_access.<clientId>.roles`。
- [ ] 生产 index.html 含注入的 `window.__OPENSANDBOX_AUTH__`，带 `Cache-Control: no-store`，前端无构建时 realm 回落。
- [ ] 浏览器登录使用 PKCE S256；token 只在 sessionStorage；刷新在到期前 60 秒触发且并发只发一次请求。
- [ ] 注销走 Keycloak `end-logout` 并清空本地 token。
- [ ] `dotnet test`、`npm test`、`npm run lint`、`npm run build`、`docker compose --env-file .env.example config --quiet` 全部通过。
