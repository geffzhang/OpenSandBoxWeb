using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class OpsEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public OpsEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task Readyz_IsAnonymous()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ready", body!.Status);
    }

    private sealed record HealthBody(string Status);
}
