using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeAuthorizationIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VolumeAuthorizationIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Scan_RequiresVolumeAdmin()
    {
        var viewer = await _factory.CreateClientWithRoles("volume:viewer")
            .PostAsync("/api/v1/volumes/scan", null);
        var admin = await _factory.CreateClient().PostAsync("/api/v1/volumes/scan", null);

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var scan = await admin.Content.ReadFromJsonAsync<VolumeScanResponse>();
        Assert.Equal("manual", scan!.Trigger);
    }

    [Fact]
    public async Task VolumeTenants_ReadableByViewer_WithStatistics()
    {
        var admin = _factory.CreateClient();
        var create = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants",
            new CreateTenantRequest("viewerco", "Viewer Co", ["viewer-ns"]));
        create.EnsureSuccessStatusCode();

        var viewer = _factory.CreateClientWithRoles("volume:viewer");
        var response = await viewer.GetAsync("/api/v1/volume-tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantListResponse>();
        var tenant = Assert.Single(body!.Items, item => item.Id == "viewerco");
        Assert.Equal("Viewer Co", tenant.DisplayName);
        Assert.Equal("active", tenant.Status);
        Assert.Equal(["viewer-ns"], tenant.Namespaces);
        Assert.Equal(0, tenant.VolumeCount);
        Assert.Equal(0, tenant.ProtectedCount);
        Assert.Equal(0, tenant.EphemeralCount);
        Assert.Equal(0, tenant.MissingCount);
        Assert.Equal(0, tenant.KeyCount);
    }
}
