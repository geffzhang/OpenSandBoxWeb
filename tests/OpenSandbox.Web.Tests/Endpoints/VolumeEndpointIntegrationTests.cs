using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

        // 租户列表强制按租户过滤，只能看到自己名下的卷。
        var tenantList = await tenant.GetFromJsonAsync<VolumeListResponse>("/api/v1/tenant/volumes");
        var tenantItem = Assert.Single(tenantList!.Items);
        Assert.Equal("pvc-1", tenantItem.ClaimName);
        Assert.Equal("goodcrew", tenantItem.Tenant);

        // 已认证的租户请求未知端点返回 404（不落到 SPA 或 401）。
        var unknownRoute = await tenant.GetAsync("/api/v1/tenant/whatever");
        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);
    }

    [Fact]
    public async Task TenantKey_CannotAccessAdminVolumeList()
    {
        var admin = _factory.CreateClientWithRoles("volume:admin");
        var key = await CreateTenantKeyAsync(admin, "keyonly");

        var response = await _factory.CreateTenantClient(key).GetAsync("/api/v1/volumes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConsoleToken_CannotAccessTenantEndpoint()
    {
        var console = _factory.CreateClientWithRoles("volume:admin");
        console.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-tenant-key");

        var response = await console.GetAsync("/api/v1/tenant/volumes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantEndpoint_WithoutKey_Returns401()
    {
        var response = await _factory.CreateAnonymousClient().PostAsJsonAsync(
            "/api/v1/tenant/volumes/register",
            new VolumeRegistrationRequest("opensandbox", []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TenantRegister_MalformedJson_Returns400()
    {
        var admin = _factory.CreateClientWithRoles("volume:admin");
        var key = await CreateTenantKeyAsync(admin, "badjson");
        var tenant = _factory.CreateTenantClient(key);

        var response = await tenant.PostAsync(
            "/api/v1/tenant/volumes/register",
            new StringContent(
                "{ \"namespace\": \"opensandbox\",", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_argument", body!.Code);
    }

    private static async Task<string> CreateTenantKeyAsync(HttpClient admin, string tenantId)
    {
        var create = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants",
            new CreateTenantRequest(tenantId, tenantId, []));
        create.EnsureSuccessStatusCode();
        var createKey = await admin.PostAsJsonAsync(
            $"/api/v1/volume-tenants/{tenantId}/keys",
            new CreateTenantKeyRequest("test"));
        createKey.EnsureSuccessStatusCode();
        var key = await createKey.Content.ReadFromJsonAsync<CreateTenantKeyResponse>();
        return key!.Key;
    }
}
