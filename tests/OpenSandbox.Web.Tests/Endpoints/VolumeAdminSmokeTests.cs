using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeAdminSmokeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VolumeAdminSmokeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Import_DryRun_ReturnsPerItemResultsWithoutWrites()
    {
        var admin = _factory.CreateClient();
        var create = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants",
            new CreateTenantRequest("importco", "Import Co", ["import-ns"]));
        create.EnsureSuccessStatusCode();
        _factory.VolumeKube.Namespaces["import-ns"] = new NamespaceInfo(
            "import-ns",
            new Dictionary<string, string> { [VolumeLabels.Managed] = "true" });
        _factory.VolumeKube.AddPvc("import-ns", "pvc-import");

        var response = await admin.PostAsJsonAsync(
            "/api/v1/volumes/import",
            new VolumeImportRequest(true,
            [
                new VolumeImportItem(
                    "importco", "import-ns", "pvc-import", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-1"),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VolumeImportResponse>();
        Assert.True(body!.DryRun);
        var result = Assert.Single(body.Results);
        Assert.Equal("import-ns", result.Namespace);
        Assert.Equal("pvc-import", result.ClaimName);
        Assert.Equal("would-register", result.Status);

        await using var db = _factory.NewVolumeDb();
        Assert.Equal(0, await db.Volumes.CountAsync(
            volume => volume.Namespace == "import-ns"));
    }

    [Fact]
    public async Task Detail_MissingClaim_Returns404()
    {
        var admin = _factory.CreateClient();

        var response = await admin.GetAsync("/api/v1/volumes/opensandbox/no-such-claim");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("not_found", body!.Code);
    }
}
