using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class FakeVolumeKubernetesClient : IVolumeKubernetesClient
{
    public Dictionary<string, NamespaceInfo> Namespaces { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<ObservedPvc>> Pvcs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<PodMount>> Pods { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailNamespaceLookups { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailPvcLists { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailPodLists { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailLabelPatches { get; } = new(StringComparer.Ordinal);
    public List<(string Namespace, string ClaimName, IReadOnlyDictionary<string, string> Labels)> Patches { get; } = [];
    public Exception? ListNamespacesException { get; set; }

    public static FakeVolumeKubernetesClient WithManagedNamespace(string name = "opensandbox")
    {
        var fake = new FakeVolumeKubernetesClient();
        fake.Namespaces[name] = new NamespaceInfo(name, new Dictionary<string, string>
        {
            [VolumeLabels.Managed] = "true",
        });
        fake.Pvcs[name] = [];
        fake.Pods[name] = [];
        return fake;
    }

    public void AddPvc(
        string namespaceName,
        string claimName,
        string uid = "uid-1",
        Dictionary<string, string>? labels = null,
        DateTimeOffset? deletionTimestamp = null)
    {
        if (!Pvcs.TryGetValue(namespaceName, out var pvcs))
            Pvcs[namespaceName] = pvcs = [];
        pvcs.Add(new ObservedPvc(
            namespaceName, claimName, uid, "Bound", "standard", "1Gi", "1Gi",
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), deletionTimestamp,
            labels ?? new Dictionary<string, string>()));
    }

    public Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct)
    {
        if (ListNamespacesException is not null) throw ListNamespacesException;
        IReadOnlyList<ManagedNamespaceInfo> result = Namespaces.Values
            .Where(ns => ns.Labels.TryGetValue(VolumeLabels.Managed, out var value) && value == "true")
            .Select(ns => new ManagedNamespaceInfo(ns.Name, ns.Labels))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct)
    {
        if (FailNamespaceLookups.Contains(name)) throw new InvalidOperationException("namespace lookup failed");
        return Task.FromResult(Namespaces.GetValueOrDefault(name));
    }

    public Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string ns, CancellationToken ct)
    {
        if (FailPvcLists.Contains(ns)) throw new InvalidOperationException("pvc list failed");
        return Task.FromResult((IReadOnlyList<ObservedPvc>)Pvcs.GetValueOrDefault(ns) ?? []);
    }

    public Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string ns, CancellationToken ct)
    {
        if (FailPodLists.Contains(ns)) throw new InvalidOperationException("pod list failed");
        return Task.FromResult((IReadOnlyList<PodMount>)Pods.GetValueOrDefault(ns) ?? []);
    }

    public Task PatchPvcLabelsAsync(
        string ns, string claimName, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (FailLabelPatches.Contains(claimName)) throw new InvalidOperationException("patch failed");
        Patches.Add((ns, claimName, labels));
        return Task.CompletedTask;
    }
}
