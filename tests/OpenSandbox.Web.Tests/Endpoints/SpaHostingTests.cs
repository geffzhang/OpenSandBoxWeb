using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

/// <summary>
/// Runs the host in Production so <see cref="WebApplicationFactory{TEntryPoint}"/>
/// honours the per-test web root (Development mode reads bin/wwwroot instead).
/// </summary>
public sealed class SpaHostingTests : IDisposable
{
    private const string IndexHtml =
        "<!doctype html><html><head><title>osb</title></head>" +
        "<body><div id=\"root\"></div></body></html>";
    private const string Authority = "https://issuer.test/realms/opensandbox";

    private readonly string _webRoot;
    private readonly SpaFactory _factory;

    public SpaHostingTests()
    {
        _webRoot = CreateWebRoot();
        _factory = new SpaFactory(_webRoot, Authority, "opensandbox-web");
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Fact]
    public async Task Root_ServesIndexWithInjectedAuthConfig()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        await AssertInjectedIndexAsync(response, "opensandbox-web");
    }

    [Fact]
    public async Task IndexHtml_ServesIndexWithInjectedAuthConfig()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/index.html");

        await AssertInjectedIndexAsync(response, "opensandbox-web");
    }

    [Fact]
    public async Task NonApiSpaPath_FallsBackToInjectedIndex()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/dashboard");

        await AssertInjectedIndexAsync(response, "opensandbox-web");
    }

    [Fact]
    public async Task UnknownTenantApiPath_ReturnsJson404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tenant/whatever");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<FallbackErrorBody>();
        Assert.NotNull(body);
        Assert.Equal("not_found", body.Code);
        Assert.Equal("接口不存在", body.Message);
    }

    [Fact]
    public async Task InjectedClientId_IsHtmlEscaped()
    {
        using var factory = new SpaFactory(_webRoot, Authority, "open<&>sandbox");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("\\u003C", html);
        Assert.Contains("\\u0026", html);
        Assert.DoesNotContain("open<&>sandbox", html);
    }

    [Fact]
    public async Task AnonymousUnknownApiRoute_Returns401WithBearerChallenge()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/no-such-admin-route");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        var body = await response.Content.ReadFromJsonAsync<FallbackErrorBody>();
        Assert.NotNull(body);
        Assert.Equal("unauthorized", body.Code);
    }

    private static async Task AssertInjectedIndexAsync(HttpResponseMessage response, string clientId)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("window.__OPENSANDBOX_AUTH__", html);
        Assert.Contains(Authority, html);
        Assert.Contains(clientId, html);
    }

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"osb-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), IndexHtml);
        return root;
    }

    private sealed class SpaFactory : WebApplicationFactory<Program>
    {
        private readonly string _webRoot;
        private readonly string _authority;
        private readonly string _clientId;

        public SpaFactory(string webRoot, string authority, string clientId)
        {
            _webRoot = webRoot;
            _authority = authority;
            _clientId = clientId;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(_webRoot);
            builder.UseSetting("Auth:Authority", _authority);
            builder.UseSetting("Auth:ClientId", _clientId);
        }
    }

    private sealed record FallbackErrorBody(string Code, string Message);
}
