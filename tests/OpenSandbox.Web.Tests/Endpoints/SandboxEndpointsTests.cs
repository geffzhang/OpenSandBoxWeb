using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using Xunit;

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
