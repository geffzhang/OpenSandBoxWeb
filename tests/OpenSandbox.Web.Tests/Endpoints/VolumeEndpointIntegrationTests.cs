using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeEndpointIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VolumeEndpointIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task VolumeList_RequiresVolumeRole()
    {
        var anonymous = await _factory.CreateAnonymousClient().GetAsync("/api/v1/volumes");
        var sandboxOnly = await _factory.CreateClientWithRoles("sandbox:admin")
            .GetAsync("/api/v1/volumes");
        var viewer = await _factory.CreateClientWithRoles("volume:viewer")
            .GetAsync("/api/v1/volumes");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sandboxOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
    }

    [Fact]
    public async Task TenantKey_CanRegister_AndAdminSeesLedger()
    {
        var admin = _factory.CreateClientWithRoles("volume:admin");
        var createTenant = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants",
            new CreateTenantRequest("goodcrew", "GoodCrew", ["opensandbox"]));
        createTenant.EnsureSuccessStatusCode();

        var createKey = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants/goodcrew/keys",
            new CreateTenantKeyRequest("integration"));
        createKey.EnsureSuccessStatusCode();
        var key = await createKey.Content.ReadFromJsonAsync<CreateTenantKeyResponse>();

        _factory.VolumeKube.AddPvc("opensandbox", "pvc-1");
        var tenant = _factory.CreateTenantClient(key!.Key);
        var register = await tenant.PostAsJsonAsync(
            "/api/v1/tenant/volumes/register",
            new VolumeRegistrationRequest("opensandbox",
            [
                new VolumeRegistrationItem(
                    "pvc-1", "employee", "3f2a", "meta", "/workspace", "business", "sb-1"),
            ]));
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<VolumeRegistrationResponse>();

        Assert.Equal(RegistrationStatuses.Registered, registered!.Results[0].Status);
        Assert.Single(_factory.VolumeKube.Patches);

        var list = await admin.GetFromJsonAsync<VolumeListResponse>("/api/v1/volumes");
        var item = Assert.Single(list!.Items, volume => volume.ClaimName == "pvc-1");
        Assert.Equal("goodcrew", item.Tenant);
        Assert.Equal(VolumeClassifications.Registered, item.Classification);
    }

    [Fact]
    public async Task TenantEndpoint_WithoutKey_Returns401()
    {
        var response = await _factory.CreateAnonymousClient().PostAsJsonAsync(
            "/api/v1/tenant/volumes/register",
            new VolumeRegistrationRequest("opensandbox", []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
