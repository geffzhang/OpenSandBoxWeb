using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record LabelWriteResult(bool Success, string? Error = null);

public sealed class VolumeLabelWriter(IVolumeKubernetesClient kube)
{
    public static IReadOnlyDictionary<string, string> BuildAllLabels(VolumeEntity volume)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VolumeLabels.Lifecycle] = volume.Lifecycle,
            [VolumeLabels.Tenant] = volume.TenantId,
            [VolumeLabels.ContractVersion] = volume.ContractVersion,
        };

        var owner = VolumeLabels.NormalizeOwner(volume.OwnerKind, volume.OwnerId);
        if (owner is not null)
            labels[VolumeLabels.Owner] = owner;

        return labels;
    }

    public Task<LabelWriteResult> ApplyAllAsync(VolumeEntity volume, CancellationToken ct) =>
        WriteAsync(volume, BuildAllLabels(volume), ct);

    public Task<LabelWriteResult> ApplyLifecycleAsync(
        VolumeEntity volume, string lifecycle, CancellationToken ct) =>
        WriteAsync(volume, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VolumeLabels.Lifecycle] = lifecycle,
        }, ct);

    private async Task<LabelWriteResult> WriteAsync(
        VolumeEntity volume,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct)
    {
        try
        {
            await kube.PatchPvcLabelsAsync(volume.Namespace, volume.ClaimName, labels, ct);
            return new LabelWriteResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LabelWriteResult(false, exception.Message);
        }
    }
}
