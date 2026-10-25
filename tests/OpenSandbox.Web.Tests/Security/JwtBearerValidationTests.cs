using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

/// <summary>
/// Exercises the real JwtBearer handler and token validation (no TestAuthHandler):
/// metadata fetch is disabled by injecting a static OpenIdConnectConfiguration that
/// carries the RS256 signing key used to mint the tokens.
/// </summary>
public sealed class JwtBearerValidationTests : IDisposable
{
    private const string Authority = "https://issuer.test/realms/opensandbox/";
    private const string Issuer = "https://issuer.test/realms/opensandbox";
    private const string ClientId = "opensandbox-web";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "opensandbox-test-key" };

    private readonly JwtBearerFactory _factory;

    public JwtBearerValidationTests()
    {
        _factory = new JwtBearerFactory(SigningKey);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task MissingAuthorizationHeader_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/console/me");

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task ValidToken_ReturnsConsoleIdentity()
    {
        using var client = CreateAuthenticatedClient(CreateToken());

        var response = await client.GetAsync("/api/v1/console/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsoleMeBody>();
        Assert.NotNull(body);
        Assert.Equal("tester", body.Username);
        Assert.Contains("sandbox:viewer", body.Roles);
    }

    [Fact]
    public async Task WrongIssuer_Returns401()
    {
        using var client = CreateAuthenticatedClient(
            CreateToken(issuer: "https://issuer.test/realms/other"));

        var response = await client.GetAsync("/api/v1/console/me");

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var now = DateTime.UtcNow;
        using var client = CreateAuthenticatedClient(
            CreateToken(expiresUtc: now.AddMinutes(-5), notBeforeUtc: now.AddMinutes(-10)));

        var response = await client.GetAsync("/api/v1/console/me");

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task WrongAzp_Returns401()
    {
        using var client = CreateAuthenticatedClient(CreateToken(azp: "other-client"));

        var response = await client.GetAsync("/api/v1/console/me");

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task ValidTokenWithoutConsoleRoles_Returns403()
    {
        using var client = CreateAuthenticatedClient(CreateToken(roles: []));

        var response = await client.GetAsync("/api/v1/sandboxes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ForbiddenBody>();
        Assert.NotNull(body);
        Assert.Equal("forbidden", body.Code);
        Assert.Empty(body.Roles);
    }

    private HttpClient CreateAuthenticatedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string CreateToken(
        string issuer = Issuer,
        string azp = ClientId,
        string[]? roles = null,
        DateTime? expiresUtc = null,
        DateTime? notBeforeUtc = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = notBeforeUtc ?? now.AddMinutes(-1),
            Expires = expiresUtc ?? now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["azp"] = azp,
                ["sub"] = "user-1",
                ["preferred_username"] = "tester",
                ["resource_access"] = new Dictionary<string, object>
                {
                    [ClientId] = new Dictionary<string, object>
                    {
                        ["roles"] = roles ?? ["sandbox:viewer"],
                    },
                },
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        var body = await response.Content.ReadFromJsonAsync<AuthErrorBody>();
        Assert.NotNull(body);
        Assert.Equal("unauthorized", body.Code);
        Assert.Equal("需要登录", body.Message);
    }

    private sealed class JwtBearerFactory : WebApplicationFactory<Program>
    {
        private readonly RsaSecurityKey _key;

        public JwtBearerFactory(RsaSecurityKey key) => _key = key;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Auth:Authority", Authority);
            builder.UseSetting("Auth:ClientId", ClientId);

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigureAll<JwtBearerOptions>(options =>
                {
                    // 不访问网络：静态配置代替 metadata 端点。
                    options.MetadataAddress = null!;
                    options.ConfigurationManager = null;
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(_key);
                    options.Configuration = configuration;
                    options.TokenValidationParameters.IssuerSigningKey = _key;
                });
            });
        }
    }

    private sealed record AuthErrorBody(string Code, string Message);

    private sealed record ConsoleMeBody(string Subject, string Username, IReadOnlyList<string> Roles);

    private sealed record ForbiddenBody(string Code, string Message, IReadOnlyList<string> Roles);
}
