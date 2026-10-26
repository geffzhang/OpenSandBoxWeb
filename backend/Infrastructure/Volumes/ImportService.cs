using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class ImportService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    TimeProvider time)
{
    private const string NullItemMessage = "卷条目为空";

    public async Task<VolumeImportResponse> ImportAsync(
        VolumeImportRequest request,
        CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0)
            throw new VolumeValidationException("items 不能为空");

        var dryRun = request.DryRun ?? true;
        var items = request.Items;

        // 预投影跳过空条目，避免后续归一化时解引用 null。
        var tenantIds = items
            .Where(item => item is not null)
            .Select(item => VolumeValidation.TenantId(item.Tenant) ?? "")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var namespaceNames = items
            .Where(item => item is not null)
            .Select(item => VolumeValidation.Namespace(item.Namespace))
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var tenants = await db.Tenants.AsNoTracking()
            .Include(tenant => tenant.Namespaces)
            .Where(tenant => tenantIds.Contains(tenant.Id))
            .ToListAsync(ct);
        var tenantIndex = tenants.ToDictionary(tenant => tenant.Id, StringComparer.Ordinal);

        var managedCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pvcCache = new Dictionary<string, Dictionary<string, ObservedPvc>>(StringComparer.Ordinal);

        foreach (var namespaceName in namespaceNames)
        {
            try
            {
                var info = await kube.GetNamespaceAsync(namespaceName, ct);
                managedCache[namespaceName] =
                    info is not null &&
                    info.Labels.TryGetValue(VolumeLabels.Managed, out var managed) &&
                    managed == "true";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new VolumeDependencyException($"Kubernetes 暂不可用: {exception.Message}");
            }
        }

        foreach (var namespaceName in managedCache
                     .Where(pair => pair.Value)
                     .Select(pair => pair.Key))
        {
            try
            {
                pvcCache[namespaceName] = (await kube.ListPvcsAsync(namespaceName, ct))
                    .ToDictionary(pvc => pvc.ClaimName, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new VolumeDependencyException($"Kubernetes 暂不可用: {exception.Message}");
            }
        }

        // 命名空间内用于 claim 冲突；租户全量用于跨命名空间 owner 冲突。
        var existing = await db.Volumes
            .Where(volume =>
                namespaceNames.Contains(volume.Namespace) ||
                tenantIds.Contains(volume.TenantId))
            .ToListAsync(ct);

        var results = new List<VolumeImportResult>();
        var pendingPatches = new List<VolumeEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = time.GetUtcNow();

        foreach (var item in items)
        {
            if (item is null)
            {
                results.Add(new VolumeImportResult(
                    "", "", RegistrationStatuses.Invalid, NullItemMessage));
                continue;
            }

            var validation = VolumeValidation.ValidateImportItem(item);
            if (validation is not null)
            {
                results.Add(new VolumeImportResult(
                    item.Namespace?.Trim() ?? "", item.ClaimName?.Trim() ?? "",
                    RegistrationStatuses.Invalid, validation));
                continue;
            }

            // 校验通过后这些值一定非空；持久化与比较统一使用归一化值。
            var tenantId = VolumeValidation.TenantId(item.Tenant)!;
            var namespaceName = VolumeValidation.Namespace(item.Namespace)!;
            var claimName = VolumeValidation.ClaimName(item.ClaimName)!;
            var key = $"{namespaceName}/{claimName}";

            if (!seen.Add(key))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "重复的导入项"));
                continue;
            }

            if (!tenantIndex.TryGetValue(tenantId, out var tenant) ||
                tenant.Status != "active")
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "租户不存在或已停用"));
                continue;
            }

            if (!tenant.Namespaces.Any(mapping => mapping.Namespace == namespaceName))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "命名空间未分配给该租户"));
                continue;
            }

            if (!managedCache.GetValueOrDefault(namespaceName))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "命名空间未标记为受管"));
                continue;
            }

            var pvcs = pvcCache[namespaceName];
            if (!pvcs.TryGetValue(claimName, out var pvc))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.NotFound, "PVC 不存在"));
                continue;
            }

            var ownerKind = VolumeValidation.Text(item.OwnerKind, VolumeValidation.OwnerKindMax)!;
            var ownerId = VolumeValidation.Text(item.OwnerId, VolumeValidation.OwnerIdMax)!;
            var lifecycle = item.Lifecycle.Trim();
            var byClaim = existing.FirstOrDefault(volume =>
                volume.Namespace == namespaceName && volume.ClaimName == claimName);

            if (byClaim is not null)
            {
                if (byClaim.TenantId != tenant.Id ||
                    byClaim.OwnerKind != ownerKind ||
                    byClaim.OwnerId != ownerId ||
                    byClaim.Lifecycle != lifecycle)
                {
                    results.Add(new VolumeImportResult(
                        namespaceName, claimName, RegistrationStatuses.Conflict, "与已有台账冲突"));
                    continue;
                }

                if (dryRun)
                {
                    results.Add(new VolumeImportResult(
                        namespaceName, claimName, "would-update", null));
                    continue;
                }

                byClaim.Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax);
                byClaim.MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax);
                byClaim.LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax);
                byClaim.LabelState = VolumeLabelStates.Pending;
                byClaim.UpdatedAt = now;
                AddEvent(byClaim, VolumeEventTypes.Imported, now);
                pendingPatches.Add(byClaim);
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Updated, null));
                continue;
            }

            var ownerRow = existing.FirstOrDefault(volume =>
                volume.TenantId == tenant.Id &&
                volume.OwnerKind == ownerKind &&
                volume.OwnerId == ownerId &&
                (volume.Namespace != namespaceName || volume.ClaimName != claimName));
            if (ownerRow is not null)
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Conflict,
                    "owner 已绑定其他 PVC"));
                continue;
            }

            var volume = new VolumeEntity
            {
                Id = Guid.CreateVersion7(),
                Namespace = namespaceName,
                ClaimName = claimName,
                TenantId = tenant.Id,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax),
                MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax),
                Lifecycle = lifecycle,
                ContractVersion = VolumeLabels.ContractVersionValue,
                LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax),
                RegisteredUid = pvc.Uid,
                State = VolumeStates.Active,
                LabelState = VolumeLabelStates.Pending,
                RegisteredAt = now,
                UpdatedAt = now,
            };

            if (dryRun)
            {
                // 预演同样占用 owner/claim，让同批次后续项得到与写入模式一致的冲突结果。
                existing.Add(volume);
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, "would-register", null));
                continue;
            }

            db.Volumes.Add(volume);
            existing.Add(volume);
            AddEvent(volume, VolumeEventTypes.Imported, now);
            pendingPatches.Add(volume);
            results.Add(new VolumeImportResult(
                namespaceName, claimName, RegistrationStatuses.Registered, null));
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);

            foreach (var volume in pendingPatches.DistinctBy(entity => entity.Id))
            {
                var write = await labels.ApplyAllAsync(volume, ct);
                if (write.Success)
                {
                    volume.LabelState = VolumeLabelStates.Applied;
                    AddEvent(volume, VolumeEventTypes.LabelApplied, time.GetUtcNow());
                    continue;
                }

                for (var index = 0; index < results.Count; index++)
                {
                    if (results[index].Namespace != volume.Namespace ||
                        results[index].ClaimName != volume.ClaimName)
                        continue;
                    results[index] = results[index] with
                    {
                        Status = RegistrationStatuses.LabelPending,
                        Message = write.Error,
                    };
                }
                AddEvent(volume, VolumeEventTypes.LabelPatchFailed, time.GetUtcNow(),
                    write.Error);
            }

            await db.SaveChangesAsync(ct);
        }

        return new VolumeImportResponse(dryRun, results);
    }

    private void AddEvent(VolumeEntity volume, string type, DateTimeOffset now, string? reason = null)
    {
        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = type,
            Actor = "admin",
            Reason = reason,
            DataJson = "{}",
            CreatedAt = now,
        });
    }
}
