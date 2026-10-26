using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class KubernetesVolumeClient : IVolumeKubernetesClient
{
    private readonly IKubernetes _client;
    private readonly VolumeManagerOptions _options;

    public KubernetesVolumeClient(IKubernetes client, VolumeManagerOptions options)
    {
        _client = client;
        _options = options;
    }

    public async Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespaceAsync(
            labelSelector: _options.ManagedNamespaceSelector,
            cancellationToken: ct);
        return list.Items.Select(MapManagedNamespace).ToArray();
    }

    public async Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct)
    {
        try
        {
            return MapNamespaceInfo(await _client.CoreV1.ReadNamespaceAsync(name, cancellationToken: ct));
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string namespaceName, CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
            namespaceName, cancellationToken: ct);
        return list.Items.Select(MapPvc).ToArray();
    }

    public async Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string namespaceName, CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespacedPodAsync(namespaceName, cancellationToken: ct);
        return list.Items
            .Where(pod => pod.Status?.Phase is not ("Succeeded" or "Failed"))
            .Select(pod => new PodMount(
                pod.Metadata.Name,
                pod.Spec?.Volumes?
                    .Where(volume => volume.PersistentVolumeClaim is not null)
                    .Select(volume => volume.PersistentVolumeClaim.ClaimName)
                    .ToArray() ?? []))
            .Where(mount => mount.ClaimNames.Count > 0)
            .ToArray();
    }

    public async Task PatchPvcLabelsAsync(
        string namespaceName,
        string claimName,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { metadata = new { labels } });
        var patch = new V1Patch(json, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(
            patch, claimName, namespaceName, cancellationToken: ct);
    }

    private static ManagedNamespaceInfo MapManagedNamespace(V1Namespace ns) =>
        new(ns.Metadata.Name, ns.Metadata.Labels is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ns.Metadata.Labels));

    private static NamespaceInfo MapNamespaceInfo(V1Namespace ns) =>
        new(ns.Metadata.Name, ns.Metadata.Labels is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ns.Metadata.Labels));

    private static ObservedPvc MapPvc(V1PersistentVolumeClaim pvc)
    {
        var metadata = pvc.Metadata;
        return new ObservedPvc(
            metadata.NamespaceProperty ?? "",
            metadata.Name,
            metadata.Uid ?? "",
            pvc.Status?.Phase ?? "Unknown",
            pvc.Spec?.StorageClassName,
            Storage(pvc.Spec?.Resources?.Requests),
            Storage(pvc.Status?.Capacity),
            ToOffset(metadata.CreationTimestamp) ?? DateTimeOffset.UnixEpoch,
            ToOffset(metadata.DeletionTimestamp),
            metadata.Labels is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata.Labels));
    }

    private static string? Storage(IDictionary<string, ResourceQuantity>? values) =>
        values is not null && values.TryGetValue("storage", out var quantity)
            ? quantity.ToString()
            : null;

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
