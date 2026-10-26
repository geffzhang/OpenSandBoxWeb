using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class InventoryScanServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static VolumeEntity Ledger(
        string claimName,
        string lifecycle = VolumeLifecycle.Business,
        string state = VolumeStates.Active,
        string? registeredUid = "uid-1",
        DateTimeOffset? lastSeenAt = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = "goodcrew",
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = lifecycle,
        ContractVersion = VolumeLabels.ContractVersionValue,
        RegisteredUid = registeredUid,
        State = state,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
        LastSeenAt = lastSeenAt,
    };

    private static Dictionary<string, string> Labels(
        string lifecycle = "business", string tenant = "goodcrew") => new()
    {
        [VolumeLabels.Lifecycle] = lifecycle,
        [VolumeLabels.Tenant] = tenant,
        [VolumeLabels.Owner] = "employee-3f2a",
        [VolumeLabels.ContractVersion] = "1",
    };

    private static InventoryScanService Service(
        TestDb test,
        FakeVolumeKubernetesClient kube,
        VolumeManagerOptions? options = null) =>
        new(test.Db, kube, new VolumeLabelWriter(kube),
            options ?? new VolumeManagerOptions { MaxConcurrentNamespaces = 2 },
            new FixedTimeProvider(Now), NullLogger<InventoryScanService>.Instance);

    [Fact]
    public async Task Scan_UpsertsObservationsAndMounts()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2", labels: Labels());
        kube.Pods["opensandbox"].Add(new PodMount("pod-1", ["pvc-1"]));
        kube.Pods["opensandbox"].Add(new PodMount("pod-done", ["pvc-2"]));
        kube.Pods["opensandbox"] = kube.Pods["opensandbox"]
            .Where(p => p.PodName != "pod-done").ToList();

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(1, summary.NamespacesSucceeded);
        Assert.Equal(2, summary.VolumeCount);
        var one = await test.Db.ClusterVolumes.SingleAsync(x => x.ClaimName == "pvc-1");
        Assert.True(one.Mounted);
        Assert.Equal(["pod-1"], one.MountedBy());
        Assert.Equal("business", one.LifecycleLabel);
    }

    [Fact]
    public async Task FailedNamespaceList_DoesNotTransitionOrDeleteObservations()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox", ClaimName = "pvc-1", Uid = "uid-1",
            Phase = "Bound", CreatedAtK8s = Now, FirstSeenAt = Now, LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.FailPvcLists.Add("opensandbox");

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Single(summary.Errors);
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.Equal(VolumeStates.Active, (await test.Db.Volumes.SingleAsync()).State);
        Assert.Equal(1, await test.Db.ClusterVolumes.CountAsync());
    }

    [Fact]
    public async Task BusinessVolumeDisappears_RecordsUnexpectedEvent()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        await test.Db.SaveChangesAsync();

        var summary = await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeStates.Unexpected, volume.StateReason);
        Assert.Equal(1, summary.NamespacesSucceeded);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task NamespaceDeleted_MarksMissingWithoutPerRowUnexpectedEvents()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Active,
        });
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeStates.NamespaceDeleted, volume.StateReason);
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance));
        Assert.False(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task NamespaceDeleted_MarksAllLedgerRowsMissingAndEmitsOneBatchEvent()
    {
        using var test = new TestDb();
        var active = Ledger("pvc-active", lastSeenAt: Now.AddMinutes(-5));
        var missing = Ledger("pvc-missing", state: VolumeStates.Missing, lastSeenAt: Now.AddMinutes(-5));
        missing.StateReason = VolumeStates.Unexpected;
        missing.MissingSince = Now.AddDays(-1);
        test.Db.Volumes.AddRange(active, missing);
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Active,
        });
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var rows = await test.Db.Volumes.ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.Equal(VolumeStates.Missing, row.State);
            Assert.Equal(VolumeStates.NamespaceDeleted, row.StateReason);
        });
        Assert.Equal(Now.AddDays(-1),
            rows.Single(row => row.ClaimName == "pvc-missing").MissingSince);
        Assert.Equal(1, await test.Db.VolumeEvents.CountAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance &&
            e.Namespace == "opensandbox"));
        Assert.False(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task PendingLabel_IsRetriedDuringScan()
    {
        using var test = new TestDb();
        var volume = Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5));
        volume.LabelState = VolumeLabelStates.Pending;
        test.Db.Volumes.Add(volume);
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(VolumeLabelStates.Applied, (await test.Db.Volumes.SingleAsync()).LabelState);
        Assert.Single(kube.Patches);
    }

    [Fact]
    public async Task RemovedPvc_DeletesObservationRow()
    {
        using var test = new TestDb();
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox", ClaimName = "pvc-gone", Uid = "uid-9",
            Phase = "Bound", CreatedAtK8s = Now, FirstSeenAt = Now, LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        Assert.Equal(0, await test.Db.ClusterVolumes.CountAsync());
    }

    [Fact]
    public async Task MissingWithNewUid_DoesNotRefreshLastSeenAt()
    {
        using var test = new TestDb();
        var volume = Ledger("pvc-1", state: VolumeStates.Missing, lastSeenAt: Now.AddMinutes(-5));
        volume.StateReason = VolumeStates.Unexpected;
        test.Db.Volumes.Add(volume);
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-2", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var stored = await test.Db.Volumes.SingleAsync();
        Assert.Equal(Now.AddMinutes(-5), stored.LastSeenAt);
        Assert.Equal(VolumeStates.Missing, stored.State);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.ReappearedWithNewUid));
    }
}
