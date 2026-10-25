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
