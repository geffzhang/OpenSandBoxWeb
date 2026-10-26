using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class RegistrationServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static RegistrationService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, kube, new VolumeLabelWriter(kube),
            new FixedTimeProvider(Now), NullLogger<RegistrationService>.Instance);

    private static async Task SeedNamespaceAsync(TestDb test)
    {
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
    }

    private static VolumeRegistrationRequest Request(
        string claimName = "pvc-1",
        string ownerKind = "employee",
        string ownerId = "3f2a",
        string lifecycle = "business",
        string namespaceName = "opensandbox")
        => new(namespaceName,
        [
            new VolumeRegistrationItem(
                claimName, ownerKind, ownerId, "meta", "/workspace", lifecycle, "sb-1"),
        ]);

    [Fact]
    public async Task Register_NewVolume_CreatesLedgerAndAppliesLabels()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal(RegistrationStatuses.Registered, result.Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal("goodcrew", volume.TenantId);
        Assert.Equal("uid-1", volume.RegisteredUid);
        Assert.Equal(VolumeStates.Active, volume.State);
        Assert.Equal(VolumeLabelStates.Applied, volume.LabelState);
        Assert.Single(kube.Patches);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e => e.Type == VolumeEventTypes.Registered));
    }

    [Fact]
    public async Task Register_SameOwnerTwice_UpdatesWithoutSecondRow()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        var service = Service(test, kube);

        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);
        var second = await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Updated, second.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_MissingPvc_ReturnsNotFoundWithoutRow()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.NotFound, response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_ClaimOwnedByOtherTenant_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "other", OwnerKind = "employee", OwnerId = "other",
            Lifecycle = VolumeLifecycle.Business, RegisteredAt = Now, UpdatedAt = Now,
            RegisteredUid = "uid-1", State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal("other", (await test.Db.Volumes.SingleAsync()).TenantId);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.RegistrationConflict));
    }

    [Fact]
    public async Task Register_SameOwnerDifferentClaim_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request("pvc-1"), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew", Request("pvc-2"), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_SameOwnerInAnotherNamespace_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox-2",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.Namespaces["opensandbox-2"] = new NamespaceInfo(
            "opensandbox-2",
            new Dictionary<string, string> { [VolumeLabels.Managed] = "true" });
        kube.AddPvc("opensandbox", "pvc-1");
        kube.AddPvc("opensandbox-2", "pvc-2", uid: "uid-2");
        var service = Service(test, kube);
        await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew",
            Request("pvc-2", namespaceName: "opensandbox-2"),
            "tenant:goodcrew",
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_LifecycleMismatch_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew", Request(lifecycle: "ephemeral"), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(VolumeLifecycle.Business, (await test.Db.Volumes.SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task Register_LabelPatchFailure_StillWritesLedger()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        kube.FailLabelPatches.Add("pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.LabelPending, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLabelStates.Pending, volume.LabelState);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.LabelPatchFailed));
    }

    [Fact]
    public async Task Register_NamespaceNotAssigned_ReturnsPerItemInvalid()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_NamespaceWithoutManagedLabel_ReturnsPerItemInvalid()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = new FakeVolumeKubernetesClient();
        kube.Namespaces["opensandbox"] = new NamespaceInfo(
            "opensandbox", new Dictionary<string, string>());
        kube.AddPvc("opensandbox", "pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
    }

    [Fact]
    public async Task Register_NullItem_ReturnsPerItemInvalid()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", new VolumeRegistrationRequest("opensandbox", [null!]),
            "tenant:goodcrew", CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal(RegistrationStatuses.Invalid, result.Status);
        Assert.Equal("卷条目为空", result.Message);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_ReactivateMissingVolume_ReturnsReactivated()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        volume.State = VolumeStates.Missing;
        volume.StateReason = VolumeStates.Unexpected;
        await test.Db.SaveChangesAsync();

        var response = await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Reactivated, response.Results[0].Status);
        Assert.Equal(VolumeStates.Active, (await test.Db.Volumes.SingleAsync()).State);
    }
}
