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
