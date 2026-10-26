using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class LifecycleServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, FakeVolumeKubernetesClient Kube)> SeedAsync()
    {
        var test = new TestDb();
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(),
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            TenantId = "goodcrew",
            OwnerKind = "employee",
            OwnerId = "3f2a",
            Lifecycle = VolumeLifecycle.Business,
            RegisteredAt = Now,
            UpdatedAt = Now,
            RegisteredUid = "uid-1",
            State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        return (test, kube);
    }

    private static LifecycleService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, new VolumeLabelWriter(kube),
            new FixedTimeProvider(Now), NullLogger<LifecycleService>.Instance);

    [Fact]
    public async Task DowngradeWithoutReason_ThrowsValidation()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        await Assert.ThrowsAsync<VolumeValidationException>(() => Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", null), false,
            "tenant:goodcrew", CancellationToken.None));
    }

    [Fact]
    public async Task DowngradeWithReason_ChangesLedgerAndOnlyLifecycleLabel()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "sandbox 已完成"), false,
            "tenant:goodcrew", CancellationToken.None);

        Assert.Equal("updated", response.Status);
        Assert.Equal(VolumeLabelStates.Applied, response.LabelState);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLifecycle.Ephemeral, volume.Lifecycle);
        Assert.Equal(["opensandbox.io/volume-lifecycle"], kube.Patches[0].Labels.Keys);
        var audit = await test.Db.VolumeEvents.SingleAsync(
            e => e.Type == VolumeEventTypes.LifecycleChanged);
        Assert.Equal("sandbox 已完成", audit.Reason);
    }

    [Fact]
    public async Task OtherTenant_CannotChangeLifecycle()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        await Assert.ThrowsAsync<VolumeNotFoundException>(() => Service(test, kube).ChangeAsync(
            "other", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "cleanup"), false,
            "tenant:other", CancellationToken.None));
    }

    [Fact]
    public async Task Admin_CanChangeAnyTenant()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            null, "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "admin cleanup"), true,
            "admin", CancellationToken.None);

        Assert.Equal("updated", response.Status);
    }

    [Fact]
    public async Task SameLifecycle_ReturnsUnchanged()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("business", null), false,
            "tenant:goodcrew", CancellationToken.None);

        Assert.Equal("unchanged", response.Status);
        Assert.Empty(kube.Patches);
    }
}
