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
        options.MetadataAddress = string.IsNullOrWhiteSpace(options.MetadataAddress)
            ? null
            : options.MetadataAddress.Trim();

        services.Configure<ConsoleAuthOptions>(configured =>
        {
            configured.Authority = options.Authority;
            configured.ClientId = options.ClientId;
            configured.RequireHttpsMetadata = options.RequireHttpsMetadata;
            configured.MetadataAddress = options.MetadataAddress;
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = options.Authority;
            // 容器内 discovery/JWKS 可走内部地址；iss 校验仍使用浏览器可见的 Authority。
            if (!string.IsNullOrEmpty(options.MetadataAddress))
                jwt.MetadataAddress = options.MetadataAddress;
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
