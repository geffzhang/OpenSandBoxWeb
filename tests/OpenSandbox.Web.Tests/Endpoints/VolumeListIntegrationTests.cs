using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeListIntegrationTests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private readonly ApiFactory _factory;

    public VolumeListIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_DefaultsToFirstPageAndCountsAfterFilter()
    {
        await using (var db = _factory.NewVolumeDb())
        {
            db.Tenants.Add(new TenantEntity
            {
                Id = "listco",
                DisplayName = "List Co",
                Status = "active",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.TenantNamespaces.Add(new TenantNamespaceEntity
            {
                Namespace = "list-ns",
                TenantId = "listco",
            });
            db.Volumes.AddRange(
                Ledger("pvc-alpha", "owner-a"),
                Ledger("pvc-beta", "owner-b"),
                Ledger("pvc-gamma", "owner-c"));
            await db.SaveChangesAsync();
        }

        var viewer = _factory.CreateClientWithRoles("volume:viewer");
        var bare = await viewer.GetFromJsonAsync<VolumeListResponse>("/api/v1/volumes");
        Assert.Equal(1, bare!.Pagination.Page);
        Assert.Equal(20, bare.Pagination.PageSize);
        Assert.Equal(3, bare.Pagination.TotalItems);
        Assert.Equal(3, bare.Items.Count);

        var filtered = await viewer.GetAsync("/api/v1/volumes?q=alpha");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var filteredBody = await filtered.Content.ReadFromJsonAsync<VolumeListResponse>();
        Assert.Equal(1, filteredBody!.Pagination.Page);
        Assert.Equal(20, filteredBody.Pagination.PageSize);
        Assert.Equal(1, filteredBody.Pagination.TotalItems);
        Assert.Equal("pvc-alpha", Assert.Single(filteredBody.Items).ClaimName);
    }

    [Fact]
    public async Task List_MalformedPage_Returns400InvalidArgument()
    {
        var viewer = _factory.CreateClientWithRoles("volume:viewer");

        var response = await viewer.GetAsync("/api/v1/volumes?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_argument", body!.Code);
    }

    private static VolumeEntity Ledger(string claimName, string ownerId) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "list-ns",
        ClaimName = claimName,
        TenantId = "listco",
        OwnerKind = "employee",
        OwnerId = ownerId,
        Lifecycle = VolumeLifecycle.Business,
        State = VolumeStates.Active,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now,
        UpdatedAt = Now,
    };
}
