using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeLabelWriterTests
{
    private static VolumeEntity Volume() => new()
    {
        Namespace = "opensandbox",
        ClaimName = "pvc-1",
        TenantId = "goodcrew",
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = VolumeLifecycle.Business,
    };

    [Fact]
    public void BuildAllLabels_IncludesOwnerOnlyWhenNormalizable()
    {
        var labels = VolumeLabelWriter.BuildAllLabels(Volume());

        Assert.Equal("business", labels[VolumeLabels.Lifecycle]);
        Assert.Equal("goodcrew", labels[VolumeLabels.Tenant]);
        Assert.Equal("1", labels[VolumeLabels.ContractVersion]);
        Assert.Equal("employee-3f2a", labels[VolumeLabels.Owner]);

        var invalid = Volume();
        invalid.OwnerId = "!!!";
        Assert.False(VolumeLabelWriter.BuildAllLabels(invalid).ContainsKey(VolumeLabels.Owner));
    }

    [Fact]
    public async Task ApplyAllAsync_WritesContractLabels()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyAllAsync(Volume(), CancellationToken.None);

        Assert.True(result.Success);
        var patch = Assert.Single(fake.Patches);
        Assert.Equal("pvc-1", patch.ClaimName);
        Assert.Equal("goodcrew", patch.Labels[VolumeLabels.Tenant]);
    }

    [Fact]
    public async Task ApplyLifecycleAsync_TouchesOnlyLifecycleLabel()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyLifecycleAsync(Volume(), VolumeLifecycle.Ephemeral, CancellationToken.None);

        Assert.True(result.Success);
        var patch = Assert.Single(fake.Patches);
        Assert.Equal(["opensandbox.io/volume-lifecycle"], patch.Labels.Keys);
        Assert.Equal("ephemeral", patch.Labels[VolumeLabels.Lifecycle]);
    }

    [Fact]
    public async Task ApplyAllAsync_ReturnsFailureInsteadOfThrowing()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        fake.FailLabelPatches.Add("pvc-1");
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyAllAsync(Volume(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("patch failed", result.Error);
    }
}
