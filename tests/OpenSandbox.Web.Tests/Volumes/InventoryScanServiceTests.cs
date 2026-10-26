using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private static ScanRunEntity SeedScanRun(TestDb test)
    {
        var run = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = "seed",
            StartedAt = Now,
            FinishedAt = Now,
        };
        test.Db.ScanRuns.Add(run);
        return run;
    }

    private static ClusterVolumeEntity Observation(Guid scanId) => new()
    {
        Namespace = "opensandbox",
        ClaimName = "pvc-1",
        Uid = "uid-1",
        Phase = "Bound",
        CreatedAtK8s = Now,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        LastScanId = scanId,
    };

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
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.FailPvcLists.Add("opensandbox");

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Single(summary.Errors);
        Assert.Equal(0, summary.NamespacesSucceeded);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Active, volume.State);
        Assert.Equal(Now.AddMinutes(-5), volume.LastSeenAt);
        Assert.Equal(1, await test.Db.ClusterVolumes.CountAsync());
        Assert.Empty(await test.Db.VolumeEvents.ToListAsync());
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
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var rows = await test.Db.Volumes.ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.Equal(VolumeStates.Missing, row.State);
            Assert.Equal(VolumeStates.NamespaceDeleted, row.StateReason);
            Assert.Equal(Now, row.UpdatedAt);
        });
        Assert.Equal(Now.AddDays(-1),
            rows.Single(row => row.ClaimName == "pvc-missing").MissingSince);
        var batch = await test.Db.VolumeEvents.SingleAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance);
        using var data = JsonDocument.Parse(batch.DataJson);
        Assert.Equal(2, data.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(VolumeStates.NamespaceDeleted, data.RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.Equal(0, await test.Db.ClusterVolumes.CountAsync());
        Assert.Equal(0, summary.VolumeCount);
        Assert.False(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task NamespaceLostManagedLabel_MarksRowsUnmanagedOnce()
    {
        using var test = new TestDb();
        test.Db.Volumes.AddRange(
            Ledger("pvc-active", lastSeenAt: Now.AddMinutes(-5)),
            Ledger("pvc-missing", state: VolumeStates.Missing, lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Active,
        });
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();
        kube.Namespaces["opensandbox"] = new NamespaceInfo(
            "opensandbox", new Dictionary<string, string>());
        kube.Pvcs["opensandbox"] = [];
        kube.Pods["opensandbox"] = [];

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var active = await test.Db.Volumes.SingleAsync(x => x.ClaimName == "pvc-active");
        var missing = await test.Db.Volumes.SingleAsync(x => x.ClaimName == "pvc-missing");
        Assert.Equal(VolumeStates.Unmanaged, active.State);
        Assert.Null(active.StateReason);
        Assert.Equal(VolumeStates.Missing, missing.State);
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.Equal(1, await test.Db.VolumeEvents.CountAsync(e =>
            e.Type == VolumeEventTypes.NamespaceUnmanaged));
    }

    [Fact]
    public async Task MassDisappearance_EmitsSingleBatchEvent()
    {
        using var test = new TestDb();
        for (var i = 0; i < 6; i++)
            test.Db.Volumes.Add(Ledger($"pvc-{i}", lastSeenAt: Now.AddMinutes(-5)));
        await test.Db.SaveChangesAsync();

        var summary = await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        Assert.Equal(0, summary.VolumeCount);
        Assert.Equal(6, await test.Db.Volumes.CountAsync(x => x.State == VolumeStates.Missing));
        var batch = await test.Db.VolumeEvents.SingleAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance);
        using var data = JsonDocument.Parse(batch.DataJson);
        Assert.Equal(6, data.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(3, data.RootElement.GetProperty("threshold").GetInt32());
    }

    [Fact]
    public async Task ConcurrentScan_ThrowsScanInProgress()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kube.ListNamespacesStarted = started;
        kube.ListNamespacesGate = gate;

        var services = new ServiceCollection();
        services.AddScoped(_ => Service(test, kube));
        using var provider = services.BuildServiceProvider();
        var coordinator = new ScanCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>());

        var first = coordinator.ScanAsync("manual", CancellationToken.None);
        await started.Task;

        await Assert.ThrowsAsync<ScanInProgressException>(() =>
            coordinator.ScanAsync("manual", CancellationToken.None));

        gate.SetResult();
        var summary = await first;
        Assert.Equal(1, summary.NamespacesSucceeded);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task DiscoveryFailure_LeavesStateUntouchedAndFinalizesScan()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.ListNamespacesException = new InvalidOperationException("discovery boom");

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(0, summary.NamespacesScanned);
        Assert.Equal(0, summary.NamespacesSucceeded);
        var error = Assert.Single(summary.Errors);
        Assert.Equal("*", error.Namespace);
        Assert.Equal("discovery boom", error.Message);
        Assert.Equal(VolumeStates.Active, (await test.Db.Volumes.SingleAsync()).State);
        Assert.Equal(1, await test.Db.ClusterVolumes.CountAsync());
        Assert.Empty(await test.Db.VolumeEvents.ToListAsync());
        var scanRun = await test.Db.ScanRuns.SingleAsync(x => x.Trigger == "manual");
        Assert.NotNull(scanRun.FinishedAt);
        Assert.Contains("discovery boom", scanRun.ErrorsJson);
    }

    [Fact]
    public async Task TerminatingPvc_EmitsTerminatingObservedOnce()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels(),
            deletionTimestamp: Now.AddMinutes(-1));

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var observation = await test.Db.ClusterVolumes.SingleAsync();
        Assert.Equal(Now.AddMinutes(-1), observation.DeletionTimestamp);
        Assert.Equal(1, await test.Db.VolumeEvents.CountAsync(e =>
            e.Type == VolumeEventTypes.TerminatingObserved));
    }

    [Fact]
    public async Task ActiveObservedRow_RefreshesLastSeenAt()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(Now, (await test.Db.Volumes.SingleAsync()).LastSeenAt);
    }

    [Fact]
    public async Task ScanRun_PersistsCounters()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var scanRun = await test.Db.ScanRuns.SingleAsync();
        Assert.Equal("manual", scanRun.Trigger);
        Assert.Equal(Now, scanRun.StartedAt);
        Assert.Equal(Now, scanRun.FinishedAt);
        Assert.Equal(1, scanRun.NamespacesScanned);
        Assert.Equal(1, scanRun.NamespacesSucceeded);
        Assert.Equal(1, scanRun.VolumeCount);
        Assert.Equal("[]", scanRun.ErrorsJson);
    }

    [Fact]
    public async Task ReactivatedEvent_UsesSystemActor()
    {
        using var test = new TestDb();
        var volume = Ledger("pvc-1", state: VolumeStates.Missing, lastSeenAt: Now.AddMinutes(-5));
        volume.StateReason = VolumeStates.Unexpected;
        test.Db.Volumes.Add(volume);
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var reactivated = await test.Db.VolumeEvents.SingleAsync(e =>
            e.Type == VolumeEventTypes.Reactivated);
        Assert.Equal("system", reactivated.Actor);
    }

    [Fact]
    public async Task AbsentUnmanagedRow_KeepsMissingSinceNull()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", state: VolumeStates.Unmanaged,
            lastSeenAt: Now.AddMinutes(-5)));
        await test.Db.SaveChangesAsync();

        await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Unmanaged, volume.State);
        Assert.Null(volume.MissingSince);
        Assert.Empty(await test.Db.VolumeEvents.ToListAsync());
    }

    [Fact]
    public async Task UnmanagedNamespaceDeleted_MarksMissingAndPurgesObservations()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", state: VolumeStates.Unmanaged,
            lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Unmanaged,
        });
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();

        var summary = await Service(test, new FakeVolumeKubernetesClient())
            .ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeStates.NamespaceDeleted, volume.StateReason);
        var namespaceRow = await test.Db.ManagedNamespaces.SingleAsync();
        Assert.Equal(ManagedNamespaceStates.Gone, namespaceRow.State);
        Assert.Equal(0, await test.Db.ClusterVolumes.CountAsync());
        Assert.Equal(0, summary.VolumeCount);
        Assert.Equal(1, await test.Db.VolumeEvents.CountAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance));
    }

    [Fact]
    public async Task UnmanagedNamespaceStillExists_DoesNotRepeatUnmanagedEvent()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", state: VolumeStates.Unmanaged,
            lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Unmanaged,
        });
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();
        kube.Namespaces["opensandbox"] = new NamespaceInfo(
            "opensandbox", new Dictionary<string, string>());
        kube.Pvcs["opensandbox"] = [];
        kube.Pods["opensandbox"] = [];

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(ManagedNamespaceStates.Unmanaged,
            (await test.Db.ManagedNamespaces.SingleAsync()).State);
        Assert.Equal(VolumeStates.Unmanaged, (await test.Db.Volumes.SingleAsync()).State);
        Assert.Empty(await test.Db.VolumeEvents.ToListAsync());
        Assert.Equal(1, await test.Db.ClusterVolumes.CountAsync());
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
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox", ClaimName = "pvc-gone", Uid = "uid-9",
            Phase = "Bound", CreatedAtK8s = Now, FirstSeenAt = Now, LastSeenAt = Now,
            LastScanId = run.Id,
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
