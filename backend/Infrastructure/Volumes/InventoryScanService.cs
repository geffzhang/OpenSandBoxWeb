using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record ScanError(string Namespace, string Message);

public sealed record ScanSummary(
    Guid ScanId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanError> Errors);

internal sealed record NamespaceFetch(
    string Namespace,
    IReadOnlyList<ObservedPvc> Pvcs,
    IReadOnlyList<PodMount> Pods,
    string? Error);

public sealed class InventoryScanService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    VolumeManagerOptions options,
    TimeProvider time,
    ILogger<InventoryScanService> logger)
{
    public async Task<ScanSummary> ScanAsync(string trigger, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var scan = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = trigger,
            StartedAt = now,
        };
        db.ScanRuns.Add(scan);
        await db.SaveChangesAsync(ct);

        var errors = new List<ScanError>();

        IReadOnlyList<ManagedNamespaceInfo> discovered;
        try
        {
            discovered = await kube.ListManagedNamespacesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(new ScanError("*", exception.Message));
            await FinishAsync(scan, 0, 0, 0, errors, ct);
            return ToSummary(scan);
        }

        var managed = await db.ManagedNamespaces
            .ToDictionaryAsync(x => x.Namespace, StringComparer.Ordinal, ct);
        var discoveredNames = discovered.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var ns in discovered)
        {
            if (managed.TryGetValue(ns.Name, out var row))
            {
                row.State = ManagedNamespaceStates.Active;
                row.LastSeenAt = now;
            }
            else
            {
                db.ManagedNamespaces.Add(new ManagedNamespaceEntity
                {
                    Namespace = ns.Name,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    State = ManagedNamespaceStates.Active,
                });
            }
        }

        foreach (var row in managed.Values.Where(x =>
                     !discoveredNames.Contains(x.Namespace) &&
                     x.State == ManagedNamespaceStates.Active))
        {
            NamespaceInfo? namespaceInfo;
            try
            {
                namespaceInfo = await kube.GetNamespaceAsync(row.Namespace, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new ScanError(row.Namespace, exception.Message));
                continue;
            }

            if (namespaceInfo is null)
            {
                var volumes = await db.Volumes
                    .Where(volume => volume.Namespace == row.Namespace)
                    .ToListAsync(ct);

                foreach (var volume in volumes)
                {
                    volume.State = VolumeStates.Missing;
                    volume.StateReason = VolumeStates.NamespaceDeleted;
                    volume.MissingSince ??= now;
                    volume.UpdatedAt = now;
                }

                if (volumes.Count > 0)
                {
                    db.VolumeEvents.Add(BatchEvent(
                        row.Namespace,
                        VolumeEventTypes.NamespaceMassDisappearance,
                        now,
                        new { count = volumes.Count, reason = VolumeStates.NamespaceDeleted }));
                }

                row.State = ManagedNamespaceStates.Gone;
            }
            else
            {
                var volumes = await db.Volumes
                    .Where(volume => volume.Namespace == row.Namespace &&
                                     volume.State != VolumeStates.Missing &&
                                     volume.State != VolumeStates.Unmanaged)
                    .ToListAsync(ct);

                foreach (var volume in volumes)
                {
                    volume.State = VolumeStates.Unmanaged;
                    volume.StateReason = null;
                    volume.UpdatedAt = now;
                }

                if (volumes.Count > 0)
                {
                    db.VolumeEvents.Add(BatchEvent(
                        row.Namespace,
                        VolumeEventTypes.NamespaceUnmanaged,
                        now,
                        new { count = volumes.Count }));
                }

                row.State = ManagedNamespaceStates.Unmanaged;
            }
        }

        await db.SaveChangesAsync(ct);

        var fetched = new List<NamespaceFetch>();
        foreach (var chunk in discovered.Select(x => x.Name)
                     .Chunk(Math.Max(1, options.MaxConcurrentNamespaces)))
        {
            fetched.AddRange(await Task.WhenAll(chunk.Select(name => FetchAsync(name, ct))));
        }

        var scanned = 0;
        var succeeded = 0;

        foreach (var fetch in fetched)
        {
            scanned++;
            if (fetch.Error is not null)
            {
                errors.Add(new ScanError(fetch.Namespace, fetch.Error));
                continue;
            }

            await ApplyNamespaceAsync(fetch, scan.Id, now, ct);
            succeeded++;
        }

        var volumeCount = await db.ClusterVolumes.CountAsync(ct);
        await FinishAsync(scan, scanned, succeeded, volumeCount, errors, ct);
        return ToSummary(scan);
    }

    private async Task<NamespaceFetch> FetchAsync(string namespaceName, CancellationToken ct)
    {
        try
        {
            var pvcsTask = kube.ListPvcsAsync(namespaceName, ct);
            var podsTask = kube.ListPodMountsAsync(namespaceName, ct);
            await Task.WhenAll(pvcsTask, podsTask);
            return new NamespaceFetch(
                namespaceName, await pvcsTask, await podsTask, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "扫描命名空间 {Namespace} 失败", namespaceName);
            return new NamespaceFetch(namespaceName, [], [], exception.Message);
        }
    }

    private async Task ApplyNamespaceAsync(
        NamespaceFetch fetch,
        Guid scanId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pvcs = fetch.Pvcs.ToDictionary(x => x.ClaimName, StringComparer.Ordinal);
        var mountedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pod in fetch.Pods)
        {
            foreach (var claim in pod.ClaimNames)
            {
                if (!mountedBy.TryGetValue(claim, out var pods))
                    mountedBy[claim] = pods = [];
                pods.Add(pod.PodName);
            }
        }

        var observations = await db.ClusterVolumes
            .Where(row => row.Namespace == fetch.Namespace)
            .ToDictionaryAsync(row => row.ClaimName, StringComparer.Ordinal, ct);

        foreach (var (claimName, pvc) in pvcs)
        {
            var mounts = mountedBy.GetValueOrDefault(claimName) ?? [];
            if (observations.TryGetValue(claimName, out var observation))
            {
                if (pvc.DeletionTimestamp is not null && observation.DeletionTimestamp is null)
                {
                    db.VolumeEvents.Add(ObservationEvent(
                        fetch.Namespace, claimName,
                        VolumeEventTypes.TerminatingObserved, now));
                }

                UpdateObservation(observation, pvc, mounts, scanId, now);
            }
            else
            {
                var row = new ClusterVolumeEntity
                {
                    Namespace = fetch.Namespace,
                    ClaimName = claimName,
                    FirstSeenAt = now,
                };
                UpdateObservation(row, pvc, mounts, scanId, now);
                db.ClusterVolumes.Add(row);

                if (pvc.DeletionTimestamp is not null)
                {
                    db.VolumeEvents.Add(ObservationEvent(
                        fetch.Namespace, claimName,
                        VolumeEventTypes.TerminatingObserved, now));
                }
            }
        }

        foreach (var stale in observations.Values.Where(row => !pvcs.ContainsKey(row.ClaimName)))
            db.ClusterVolumes.Remove(stale);

        var volumes = await db.Volumes
            .Where(volume => volume.Namespace == fetch.Namespace)
            .ToListAsync(ct);

        var missingCount = 0;

        foreach (var volume in volumes)
        {
            var pvc = pvcs.GetValueOrDefault(volume.ClaimName);
            var observed = pvc is null ? null : new ObservedFacts(pvc.Uid, pvc.Labels);
            var wasActive = volume.State == VolumeStates.Active;

            var result = VolumeReconciliation.Reconcile(new ReconcileInput(
                TenantId: volume.TenantId,
                Lifecycle: volume.Lifecycle,
                State: volume.State,
                StateReason: volume.StateReason,
                RegisteredUid: volume.RegisteredUid,
                LastSeenAt: volume.LastSeenAt,
                LabelState: volume.LabelState,
                Violations: volume.Violations(),
                ExpectedOwnerLabel: VolumeLabels.NormalizeOwner(volume.OwnerKind, volume.OwnerId),
                Observed: observed,
                NamespaceScanSucceeded: true));

            volume.State = result.State;
            volume.StateReason = result.StateReason;
            volume.SetViolations(result.Violations);
            volume.UpdatedAt = now;

            if (result.ObservedThisScan && result.State == VolumeStates.Active)
                volume.LastSeenAt = now;
            if (result.State == VolumeStates.Active)
                volume.MissingSince = null;
            else
                volume.MissingSince ??= now;

            if (wasActive && result.State == VolumeStates.Missing)
                missingCount++;

            foreach (var eventType in result.Events)
            {
                AddVolumeEvent(volume, eventType, "scanner", now, result.StateReason,
                    new { violations = result.Violations });
            }

            if (volume.LabelState == VolumeLabelStates.Pending && pvc is not null)
            {
                var write = await labels.ApplyAllAsync(volume, ct);
                if (write.Success)
                {
                    volume.LabelState = VolumeLabelStates.Applied;
                    AddVolumeEvent(volume, VolumeEventTypes.LabelApplied, "scanner", now);
                }
                else
                {
                    AddVolumeEvent(volume, VolumeEventTypes.LabelPatchFailed, "scanner", now, write.Error);
                }
            }
        }

        var threshold = Math.Max(3, (int)Math.Ceiling(volumes.Count * 0.5));
        if (volumes.Count > 0 && missingCount >= threshold)
        {
            db.VolumeEvents.Add(BatchEvent(
                fetch.Namespace,
                VolumeEventTypes.NamespaceMassDisappearance,
                now,
                new { count = missingCount, threshold }));
        }

        await db.SaveChangesAsync(ct);
    }

    private static void UpdateObservation(
        ClusterVolumeEntity row,
        ObservedPvc pvc,
        IReadOnlyList<string> mountedBy,
        Guid scanId,
        DateTimeOffset now)
    {
        row.Uid = pvc.Uid;
        row.Phase = pvc.Phase;
        row.StorageClass = pvc.StorageClass;
        row.RequestedStorage = pvc.RequestedStorage;
        row.Capacity = pvc.Capacity;
        row.CreatedAtK8s = pvc.CreatedAt;
        row.DeletionTimestamp = pvc.DeletionTimestamp;
        row.Mounted = mountedBy.Count > 0;
        row.MountedByJson = JsonColumn.Write(
            mountedBy.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        row.LabelsJson = JsonColumn.Write(pvc.Labels);
        row.LifecycleLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Lifecycle);
        row.TenantLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Tenant);
        row.OwnerLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Owner);
        row.ContractVersionLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.ContractVersion);
        row.LastScanId = scanId;
        row.LastSeenAt = now;
    }

    private void AddVolumeEvent(
        VolumeEntity volume,
        string type,
        string actor,
        DateTimeOffset now,
        string? reason = null,
        object? data = null)
    {
        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = type,
            Actor = actor,
            Reason = reason,
            DataJson = data is null ? "{}" : JsonColumn.Write(data),
            CreatedAt = now,
        });
    }

    private static VolumeEventEntity ObservationEvent(
        string namespaceName,
        string claimName,
        string type,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = namespaceName,
        ClaimName = claimName,
        Type = type,
        Actor = "scanner",
        DataJson = "{}",
        CreatedAt = now,
    };

    private static VolumeEventEntity BatchEvent(
        string namespaceName,
        string type,
        DateTimeOffset now,
        object data) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = namespaceName,
        ClaimName = "*",
        Type = type,
        Actor = "scanner",
        DataJson = JsonColumn.Write(data),
        CreatedAt = now,
    };

    private async Task FinishAsync(
        ScanRunEntity scan,
        int scanned,
        int succeeded,
        int volumeCount,
        List<ScanError> errors,
        CancellationToken ct)
    {
        scan.FinishedAt = time.GetUtcNow();
        scan.NamespacesScanned = scanned;
        scan.NamespacesSucceeded = succeeded;
        scan.VolumeCount = volumeCount;
        scan.ErrorsJson = JsonColumn.Write(errors);
        await db.SaveChangesAsync(ct);
    }

    private static ScanSummary ToSummary(ScanRunEntity scan) => new(
        scan.Id,
        scan.Trigger,
        scan.StartedAt,
        scan.FinishedAt ?? scan.StartedAt,
        scan.NamespacesScanned,
        scan.NamespacesSucceeded,
        scan.VolumeCount,
        JsonColumn.Read<List<ScanError>>(scan.ErrorsJson, []));
}
