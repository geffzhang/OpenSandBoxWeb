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
    public async Task LedgerRows_AreNotDeletedByObservationCleanup()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Volume());
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            Uid = "uid-1",
            Phase = "Bound",
            CreatedAtK8s = Now,
            FirstSeenAt = Now,
            LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        test.Db.ClusterVolumes.RemoveRange(test.Db.ClusterVolumes);
        await test.Db.SaveChangesAsync();

        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }
}
