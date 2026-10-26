using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeQueriesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

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

    private static VolumeEntity Ledger(
        string claimName = "pvc-1",
        string tenant = "goodcrew",
        string state = VolumeStates.Active,
        string? registeredUid = "uid-1") => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = tenant,
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = VolumeLifecycle.Business,
        RegisteredUid = registeredUid,
        State = state,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now,
        UpdatedAt = Now,
        LastSeenAt = Now,
    };

    private static ClusterVolumeEntity Observation(
        Guid lastScanId,
        string claimName = "pvc-1",
        string uid = "uid-1",
        string? lifecycle = "business",
        bool mounted = false,
        string namespaceName = "opensandbox",
        string? tenantLabel = null) => new()
    {
        Namespace = namespaceName,
        ClaimName = claimName,
        Uid = uid,
        Phase = "Bound",
        StorageClass = "standard",
        Capacity = "1Gi",
        CreatedAtK8s = Now,
        Mounted = mounted,
        MountedByJson = mounted ? "[\"pod-1\"]" : "[]",
        LabelsJson = lifecycle is null
            ? "{}"
            : JsonColumn.Write(new Dictionary<string, string>
            {
                [VolumeLabels.Lifecycle] = lifecycle,
            }),
        LifecycleLabel = lifecycle,
        TenantLabel = tenantLabel,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        LastScanId = lastScanId,
    };

    [Fact]
    public async Task List_MergesLedgerAndObservations()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.Volumes.Add(Ledger());
        test.Db.ClusterVolumes.AddRange(
            Observation(run.Id, "pvc-1"),
            Observation(run.Id, "pvc-2", uid: "uid-2", lifecycle: "ephemeral"),
            Observation(run.Id, "pvc-3", uid: "uid-3", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 1, PageSize = 20 }, null, CancellationToken.None);

        Assert.Equal(3, response.Items.Count);
        Assert.Equal(3, response.Pagination.TotalItems);
        Assert.Equal(VolumeClassifications.Registered,
            response.Items.Single(item => item.ClaimName == "pvc-1").Classification);
        Assert.Equal(VolumeClassifications.Unregistered,
            response.Items.Single(item => item.ClaimName == "pvc-2").Classification);
        Assert.Equal(VolumeClassifications.Unlabeled,
            response.Items.Single(item => item.ClaimName == "pvc-3").Classification);
        Assert.Equal("ephemeral",
            response.Items.Single(item => item.ClaimName == "pvc-2").Lifecycle);
    }

    [Fact]
    public async Task List_FilterUsesTotalBeforePaging()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        for (var index = 0; index < 25; index++)
            test.Db.ClusterVolumes.Add(
                Observation(run.Id, $"pvc-{index:00}", uid: $"uid-{index}"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 3, PageSize = 10 }, null, CancellationToken.None);

        Assert.Equal(25, response.Pagination.TotalItems);
        Assert.Equal(3, response.Pagination.TotalPages);
        Assert.Equal(5, response.Items.Count);
        Assert.False(response.Pagination.HasNextPage);
    }

    [Fact]
    public async Task List_OutOfRangePage_IsEmptyButTotalRemains()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 5, PageSize = 10 }, null, CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Pagination.TotalItems);
    }

    [Fact]
    public async Task List_ClassificationAndMountedFilters()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.AddRange(
            Observation(run.Id, "pvc-1", mounted: true),
            Observation(run.Id, "pvc-2", uid: "uid-2", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var mounted = await queries.ListAsync(
            new VolumeListQuery { Classification = "unregistered", Mounted = true },
            null, CancellationToken.None);

        Assert.Single(mounted.Items);
        Assert.Equal("pvc-1", mounted.Items[0].ClaimName);
    }

    [Fact]
    public async Task List_ForcedTenant_OnlyShowsTenantLedger()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.Volumes.Add(Ledger());
        test.Db.Volumes.Add(Ledger("pvc-2", tenant: "other"));
        test.Db.ClusterVolumes.Add(Observation(run.Id, "pvc-3", uid: "uid-3"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery(), "goodcrew", CancellationToken.None);

        Assert.Single(response.Items);
        Assert.Equal("pvc-1", response.Items[0].ClaimName);
    }

    [Fact]
    public async Task Get_ObservedButUnregistered_IsVisibleToAdminOnly()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(
            Observation(run.Id, "pvc-9", uid: "uid-9", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var admin = await queries.GetAsync(
            "opensandbox", "pvc-9", null, CancellationToken.None);
        var tenant = await queries.GetAsync(
            "opensandbox", "pvc-9", "goodcrew", CancellationToken.None);

        Assert.NotNull(admin);
        Assert.Equal(VolumeClassifications.Unlabeled, admin!.Volume.Classification);
        Assert.Null(tenant);
    }

    [Fact]
    public async Task Get_Events_AreNewestFirst_AndLimitedTo100()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger());
        for (var index = 0; index < 105; index++)
        {
            test.Db.VolumeEvents.Add(new VolumeEventEntity
            {
                Id = Guid.CreateVersion7(),
                Namespace = "opensandbox",
                ClaimName = "pvc-1",
                Type = VolumeEventTypes.Updated,
                Actor = "test",
                DataJson = "{}",
                CreatedAt = Now.AddMinutes(index),
            });
        }
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var detail = await queries.GetAsync(
            "opensandbox", "pvc-1", null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(100, detail!.Events.Count);
        Assert.Equal(Now.AddMinutes(104), detail.Events[0].CreatedAt);
        Assert.Equal(Now.AddMinutes(5), detail.Events[99].CreatedAt);
    }

    [Fact]
    public async Task Report_CountsAndTruncatesAt200()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        for (var index = 0; index < 201; index++)
            test.Db.ClusterVolumes.Add(
                Observation(run.Id, $"pvc-{index:000}", uid: $"uid-{index}", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var report = await queries.ReportAsync(CancellationToken.None);

        Assert.Equal(201, report.Counts.Observed);
        Assert.Equal(201, report.Counts.Unlabeled);
        Assert.True(report.Truncated);
        Assert.Equal(200, report.Violations.Count);
        Assert.Equal(0m, report.Coverage.LabeledPercent);
        Assert.Equal("pvc-000", report.Violations[0].ClaimName);
        Assert.Equal("pvc-199", report.Violations[199].ClaimName);
        Assert.DoesNotContain(report.Violations, item => item.ClaimName == "pvc-200");
        Assert.Equal("未登记租户", Assert.Single(report.ByTenant).Key);
    }

    [Fact]
    public async Task List_TenantSort_UsesDisplayNameNotTenantId()
    {
        using var test = new TestDb();
        test.Db.Tenants.AddRange(
            new TenantEntity
            {
                Id = "t1", DisplayName = "Zulu", Status = "active",
                CreatedAt = Now, UpdatedAt = Now,
            },
            new TenantEntity
            {
                Id = "t2", DisplayName = "Alpha", Status = "active",
                CreatedAt = Now, UpdatedAt = Now,
            });
        test.Db.Volumes.AddRange(
            Ledger("pvc-t1", tenant: "t1"),
            Ledger("pvc-t2", tenant: "t2"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var ascending = await queries.ListAsync(
            new VolumeListQuery { Sort = "tenant", Order = "asc" }, null, CancellationToken.None);
        var descending = await queries.ListAsync(
            new VolumeListQuery { Sort = "tenant", Order = "desc" }, null, CancellationToken.None);

        Assert.Equal(["pvc-t2", "pvc-t1"], ascending.Items.Select(item => item.ClaimName));
        Assert.Equal(["pvc-t1", "pvc-t2"], descending.Items.Select(item => item.ClaimName));
    }

    [Fact]
    public async Task List_HugePageNumber_ReturnsEmptyPageNotFirstPage()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.ClusterVolumes.Add(Observation(run.Id));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = int.MaxValue, PageSize = 200 },
            null, CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Pagination.TotalItems);
    }

    [Fact]
    public async Task List_ForcedTenant_ExcludesObservationOnlyOutsideAssignedNamespaces()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox",
            TenantId = "goodcrew",
        });
        test.Db.ClusterVolumes.Add(Observation(
            run.Id, "pvc-1", namespaceName: "elsewhere", tenantLabel: "goodcrew"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var tenant = await queries.ListAsync(
            new VolumeListQuery(), "goodcrew", CancellationToken.None);
        var admin = await queries.ListAsync(
            new VolumeListQuery(), null, CancellationToken.None);

        Assert.Empty(tenant.Items);
        var item = Assert.Single(admin.Items);
        Assert.Equal("elsewhere", item.Namespace);
    }

    [Fact]
    public async Task Report_UnknownTenant_OnlyForObservationOnlyRows()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.Volumes.Add(Ledger());
        test.Db.ClusterVolumes.AddRange(
            Observation(run.Id, "pvc-1", tenantLabel: "ghost"),
            Observation(run.Id, "pvc-2", uid: "uid-2", tenantLabel: "ghost"),
            Observation(run.Id, "pvc-3", uid: "uid-3", tenantLabel: " "));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var report = await queries.ReportAsync(CancellationToken.None);

        var unknown = report.Violations
            .Where(item => item.Type == "unknown-tenant:ghost")
            .ToArray();
        var violation = Assert.Single(unknown);
        Assert.Equal("pvc-2", violation.ClaimName);
        Assert.Equal("未知租户标签: ghost", violation.Message);
    }

    [Fact]
    public async Task List_ObservationOnlyUnknownTenant_AppendsViolation()
    {
        using var test = new TestDb();
        var run = SeedScanRun(test);
        test.Db.Volumes.Add(Ledger());
        test.Db.ClusterVolumes.AddRange(
            Observation(run.Id, "pvc-1", tenantLabel: "ghost"),
            Observation(run.Id, "pvc-2", uid: "uid-2", tenantLabel: "ghost"),
            Observation(run.Id, "pvc-3", uid: "uid-3", tenantLabel: " "),
            Observation(run.Id, "pvc-4", uid: "uid-4", tenantLabel: "goodcrew"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery(), null, CancellationToken.None);

        Assert.Equal(["unknown-tenant:ghost"],
            response.Items.Single(item => item.ClaimName == "pvc-2").Violations);
        Assert.Empty(response.Items.Single(item => item.ClaimName == "pvc-1").Violations);
        Assert.Empty(response.Items.Single(item => item.ClaimName == "pvc-3").Violations);
        Assert.Empty(response.Items.Single(item => item.ClaimName == "pvc-4").Violations);
    }

    [Fact]
    public async Task Report_MissingBeforeObserved_EmitsViolation()
    {
        using var test = new TestDb();
        var volume = Ledger(state: VolumeStates.Missing);
        volume.StateReason = VolumeStates.NeverObserved;
        volume.LastSeenAt = null;
        test.Db.Volumes.Add(volume);
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var report = await queries.ReportAsync(CancellationToken.None);

        var violation = Assert.Single(report.Violations);
        Assert.Equal("missing-before-observed", violation.Type);
        Assert.Equal("opensandbox", violation.Namespace);
        Assert.Equal("pvc-1", violation.ClaimName);
        Assert.Equal("注册后从未观测即消失", violation.Message);
        Assert.Equal(1, report.Counts.Missing);
    }

    [Fact]
    public async Task Report_LastScan_IsNewestByStartedAt()
    {
        using var test = new TestDb();
        var older = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = "seed",
            StartedAt = Now.AddMinutes(-10),
            FinishedAt = Now.AddMinutes(-9),
        };
        var newer = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = "manual",
            StartedAt = Now,
            FinishedAt = Now,
        };
        test.Db.ScanRuns.AddRange(older, newer);
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var report = await queries.ReportAsync(CancellationToken.None);

        Assert.NotNull(report.LastScan);
        Assert.Equal(newer.Id, report.LastScan!.Id);
    }
}
