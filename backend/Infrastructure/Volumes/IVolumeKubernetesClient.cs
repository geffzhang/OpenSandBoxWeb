namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record ManagedNamespaceInfo(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

public sealed record NamespaceInfo(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ObservedPvc(
    string Namespace,
    string ClaimName,
    string Uid,
    string Phase,
    string? StorageClass,
    string? RequestedStorage,
    string? Capacity,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels);

public sealed record PodMount(
    string PodName,
    IReadOnlyList<string> ClaimNames);

public interface IVolumeKubernetesClient
{
    Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct);

    Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string namespaceName, CancellationToken ct);

    Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string namespaceName, CancellationToken ct);

    Task PatchPvcLabelsAsync(
        string namespaceName,
        string claimName,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct);
}
