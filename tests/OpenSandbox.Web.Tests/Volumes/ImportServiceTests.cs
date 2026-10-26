using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ImportServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, FakeVolumeKubernetesClient Kube)> SeedAsync()
    {
        var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        return (test, kube);
    }

    private static ImportService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, kube, new VolumeLabelWriter(kube), new FixedTimeProvider(Now),
            NullLogger<ImportService>.Instance);

    private static VolumeImportRequest Request(bool? dryRun = null) => new(
        dryRun,
        [new VolumeImportItem(
            "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
            "meta", "/workspace", "business", "sb-1")]);

    [Fact]
    public async Task Import_DefaultsToDryRunWithoutWrites()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.True(response.DryRun);
        Assert.Equal("would-register", response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
        Assert.Empty(kube.Patches);
    }

    [Fact]
    public async Task Import_WriteMode_CreatesLedgerAndPatchesLabels()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        Assert.False(response.DryRun);
        Assert.Equal(RegistrationStatuses.Registered, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLabelStates.Applied, volume.LabelState);
        Assert.Single(kube.Patches);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.Imported));
    }

    [Fact]
    public async Task Import_DryRunExistingOwner_ReturnsWouldUpdate()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        await Service(test, kube).ImportAsync(Request(dryRun: false), CancellationToken.None);

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal("would-update", response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_DryRunSameOwnerDifferentClaims_SecondIsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2");

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(true,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-1"),
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-2", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-2"),
            ]),
            CancellationToken.None);

        Assert.Equal("would-register", response.Results[0].Status);
        Assert.Equal(RegistrationStatuses.Conflict, response.Results[1].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_SameOwnerInAnotherNamespace_ReturnsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox-2",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        kube.Namespaces["opensandbox-2"] = new NamespaceInfo(
            "opensandbox-2",
            new Dictionary<string, string> { [VolumeLabels.Managed] = "true" });
        kube.AddPvc("opensandbox-2", "pvc-2", uid: "uid-2");
        await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox-2", "pvc-2", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-2"),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_OtherTenantOwnsClaim_ReturnsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "other", OwnerKind = "employee", OwnerId = "other",
            Lifecycle = VolumeLifecycle.Business, RegisteredAt = Now, UpdatedAt = Now,
            RegisteredUid = "uid-1", State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal("other", (await test.Db.Volumes.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Import_UnassignedNamespace_ReturnsInvalid()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.TenantNamespaces.RemoveRange(test.Db.TenantNamespaces);
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
    }

    [Fact]
    public async Task Import_MissingPvc_ReturnsNotFound()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.Pvcs["opensandbox"].Clear();

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.NotFound, response.Results[0].Status);
    }

    [Fact]
    public async Task Import_NullItem_ReturnsInvalidWithoutWrites()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(true, [null!]), CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal(RegistrationStatuses.Invalid, result.Status);
        Assert.Equal("卷条目为空", result.Message);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
        Assert.Empty(kube.Patches);
    }

    [Fact]
    public async Task Import_WriteMode_PersistsNormalizedValues()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "  goodcrew  ", "  opensandbox  ", "  pvc-1  ",
                    "  employee  ", "  3f2a  ", "  meta  ", "  /workspace  ",
                    "  business  ", "  sb-1  "),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Registered, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal("goodcrew", volume.TenantId);
        Assert.Equal("opensandbox", volume.Namespace);
        Assert.Equal("pvc-1", volume.ClaimName);
        Assert.Equal("employee", volume.OwnerKind);
        Assert.Equal("3f2a", volume.OwnerId);
        Assert.Equal("meta", volume.Role);
        Assert.Equal("/workspace", volume.MountPath);
        Assert.Equal("business", volume.Lifecycle);
        Assert.Equal("sb-1", volume.LastSandboxId);
    }

    [Fact]
    public async Task Import_LabelFailure_OnlyUpdatesOwningResult()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.FailLabelPatches.Add("pvc-1");

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-1"),
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-1"),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.LabelPending, response.Results[0].Status);
        Assert.Equal("patch failed", response.Results[0].Message);
        Assert.Equal(RegistrationStatuses.Invalid, response.Results[1].Status);
        Assert.Equal("重复的导入项", response.Results[1].Message);
    }

    [Fact]
    public async Task Import_WriteMode_MissingVolume_ReactivatesWithObservedUid()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "goodcrew", OwnerKind = "employee", OwnerId = "3f2a",
            Lifecycle = VolumeLifecycle.Business, RegisteredUid = "uid-old",
            State = VolumeStates.Missing, StateReason = VolumeStates.Unexpected,
            MissingSince = Now.AddDays(-2), LabelState = VolumeLabelStates.Applied,
            ViolationsJson = "[\"lifecycle-mismatch:ephemeral\"]",
            RegisteredAt = Now.AddDays(-10), UpdatedAt = Now.AddDays(-2),
        });
        await test.Db.SaveChangesAsync();
        kube.Pvcs["opensandbox"].Clear();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-new");

        var response = await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Reactivated, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Active, volume.State);
        Assert.Equal("uid-new", volume.RegisteredUid);
        Assert.Null(volume.StateReason);
        Assert.Null(volume.MissingSince);
        Assert.Empty(volume.Violations());
        Assert.Equal(VolumeLabelStates.Applied, volume.LabelState);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.Imported));
    }

    [Fact]
    public async Task Import_WriteMode_MissingVolume_AppliesRequestedLifecycle()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "goodcrew", OwnerKind = "employee", OwnerId = "3f2a",
            Lifecycle = VolumeLifecycle.Business, RegisteredUid = "uid-old",
            State = VolumeStates.Missing, StateReason = VolumeStates.Unexpected,
            LabelState = VolumeLabelStates.Applied,
            RegisteredAt = Now.AddDays(-10), UpdatedAt = Now.AddDays(-2),
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "ephemeral", "sb-1"),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Reactivated, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Active, volume.State);
        Assert.Equal(VolumeLifecycle.Ephemeral, volume.Lifecycle);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.Imported));
        var lifecycleEvent = Assert.Single(await test.Db.VolumeEvents
            .Where(e => e.Type == VolumeEventTypes.LifecycleChanged).ToListAsync());
        Assert.Equal("reactivation", lifecycleEvent.Reason);
        Assert.Single(kube.Patches);
        Assert.Equal(VolumeLifecycle.Ephemeral,
            kube.Patches[^1].Labels[VolumeLabels.Lifecycle]);
    }

    [Fact]
    public async Task Import_DryRun_MissingVolumeWithDifferentLifecycle_ReturnsWouldUpdate()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "goodcrew", OwnerKind = "employee", OwnerId = "3f2a",
            Lifecycle = VolumeLifecycle.Business, RegisteredUid = "uid-old",
            State = VolumeStates.Missing, StateReason = VolumeStates.Unexpected,
            LabelState = VolumeLabelStates.Applied,
            RegisteredAt = Now.AddDays(-10), UpdatedAt = Now.AddDays(-2),
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(true,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "ephemeral", "sb-1"),
            ]),
            CancellationToken.None);

        Assert.Equal("would-update", response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeLifecycle.Business, volume.Lifecycle);
        Assert.Empty(kube.Patches);
    }

    [Fact]
    public async Task Import_WriteMode_ActiveLifecycleMismatch_ReturnsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        await Service(test, kube).ImportAsync(Request(dryRun: false), CancellationToken.None);

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "ephemeral", "sb-1"),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(VolumeLifecycle.Business, (await test.Db.Volumes.SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task Import_NamespaceLookupFailure_ThrowsDependencyWithoutInternalText()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.FailNamespaceLookups.Add("opensandbox");

        var exception = await Assert.ThrowsAsync<VolumeDependencyException>(() =>
            Service(test, kube).ImportAsync(Request(), CancellationToken.None));

        Assert.Equal("Kubernetes 暂不可用", exception.Message);
        Assert.DoesNotContain("namespace lookup failed", exception.Message);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }
}
