using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeDbContextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static VolumeEntity Volume(string claimName = "pvc-1", string tenant = "goodcrew") => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = tenant,
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Role = "meta",
        MountPath = "/workspace",
        Lifecycle = VolumeLifecycle.Business,
        ContractVersion = VolumeLabels.ContractVersionValue,
        State = VolumeStates.Active,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now,
        UpdatedAt = Now,
    };

    [Fact]
    public async Task NamespaceAndClaimName_AreUnique()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Volume());
        await test.Db.SaveChangesAsync();

        test.Db.Volumes.Add(Volume(tenant: "other"));
        await Assert.ThrowsAsync<DbUpdateException>(() => test.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Namespace_BelongsToOnlyOneTenant()
    {
        using var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity { Namespace = "opensandbox", TenantId = "goodcrew" });
        await test.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            test.Db.Database.ExecuteSqlRawAsync(
                "INSERT INTO tenant_namespaces (Namespace, TenantId) VALUES ('opensandbox', 'other')"));
    }

    [Fact]
    public void Violations_RoundTripAsSortedJson()
    {
        var volume = Volume();
        volume.SetViolations(["tenant-mismatch:other", "labels-missing"]);

        Assert.Equal("[\"labels-missing\",\"tenant-mismatch:other\"]", volume.ViolationsJson);
        Assert.True(volume.Violations().SetEquals(["labels-missing", "tenant-mismatch:other"]));
    }

    [Fact]
    public void ObservationHelpers_RoundTripJson()
    {
        var observation = new ClusterVolumeEntity
        {
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            LabelsJson = "{\"opensandbox.io/volume-lifecycle\":\"business\"}",
            MountedByJson = "[\"pod-1\"]",
        };

        Assert.Equal("business", observation.Labels()[VolumeLabels.Lifecycle]);
        Assert.Equal(["pod-1"], observation.MountedBy());
    }

    [Fact]
    public void Model_EventTextColumnsHaveNoMaxLength()
    {
        using var test = new TestDb();
        var eventType = test.Db.Model.FindEntityType(typeof(VolumeEventEntity))!;

        Assert.Null(eventType.FindProperty(nameof(VolumeEventEntity.Actor))!.GetMaxLength());
        Assert.Null(eventType.FindProperty(nameof(VolumeEventEntity.Reason))!.GetMaxLength());
        Assert.True(eventType.FindProperty(nameof(VolumeEventEntity.Reason))!.IsNullable);
    }

    [Fact]
    public void Model_JsonColumnsUseSpecNames()
    {
        using var test = new TestDb();

        Assert.Equal("Violations", ColumnName<VolumeEntity>(
            test, nameof(VolumeEntity.ViolationsJson)));
        Assert.Equal("MountedBy", ColumnName<ClusterVolumeEntity>(
            test, nameof(ClusterVolumeEntity.MountedByJson)));
        Assert.Equal("Labels", ColumnName<ClusterVolumeEntity>(
            test, nameof(ClusterVolumeEntity.LabelsJson)));
        Assert.Equal("Errors", ColumnName<ScanRunEntity>(
            test, nameof(ScanRunEntity.ErrorsJson)));
        Assert.Equal("Data", ColumnName<VolumeEventEntity>(
            test, nameof(VolumeEventEntity.DataJson)));
    }

    [Fact]
    public void Model_LastScanIdReferencesScanRuns()
    {
        using var test = new TestDb();
        var observationType = test.Db.Model.FindEntityType(typeof(ClusterVolumeEntity))!;
        var foreignKey = Assert.Single(observationType.GetForeignKeys(),
            key => key.Properties.Any(property => property.Name == nameof(ClusterVolumeEntity.LastScanId)));

        Assert.Equal(typeof(ScanRunEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public async Task EventActor_SeventyCharacters_RoundTrips()
    {
        using var test = new TestDb();
        var actor = "tenant:" + new string('a', 63);
        Assert.Equal(70, actor.Length);
        test.Db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            Type = VolumeEventTypes.Registered,
            Actor = actor,
            CreatedAt = Now,
        });
        await test.Db.SaveChangesAsync();

        test.Db.ChangeTracker.Clear();
        Assert.Equal(actor, (await test.Db.VolumeEvents.SingleAsync()).Actor);
    }

    private static string? ColumnName<TEntity>(TestDb test, string propertyName) =>
        test.Db.Model.FindEntityType(typeof(TEntity))!.FindProperty(propertyName)!.GetColumnName();

    [Fact]
    public async Task LedgerRows_AreNotDeletedByObservationCleanup()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Volume());
        var run = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = "seed",
            StartedAt = Now,
            FinishedAt = Now,
        };
        test.Db.ScanRuns.Add(run);
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            Uid = "uid-1",
            Phase = "Bound",
            CreatedAtK8s = Now,
            FirstSeenAt = Now,
            LastSeenAt = Now,
            LastScanId = run.Id,
        });
        await test.Db.SaveChangesAsync();

        test.Db.ClusterVolumes.RemoveRange(test.Db.ClusterVolumes);
        await test.Db.SaveChangesAsync();

        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }
}
