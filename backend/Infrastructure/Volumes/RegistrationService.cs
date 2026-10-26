using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class RegistrationService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    TimeProvider time,
    ILogger<RegistrationService> logger)
{
    private const string NullItemMessage = "卷条目为空";

    public async Task<VolumeRegistrationResponse> RegisterAsync(
        string tenantId,
        VolumeRegistrationRequest request,
        string actor,
        CancellationToken ct)
    {
        if (request.Volumes is null || request.Volumes.Count == 0)
            throw new VolumeValidationException("volumes 不能为空");

        var namespaceName = VolumeValidation.Namespace(request.Namespace)
            ?? throw new VolumeValidationException("namespace 不合法");

        var tenant = await db.Tenants.AsNoTracking()
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity =>
                entity.Id == tenantId && entity.Status == "active", ct)
            ?? throw new VolumeUnauthorizedException("租户不存在或已停用");

        if (!tenant.Namespaces.Any(mapping => mapping.Namespace == namespaceName))
            return InvalidAll(request, "命名空间未分配给该租户");

        NamespaceInfo? namespaceInfo;
        try
        {
            namespaceInfo = await kube.GetNamespaceAsync(namespaceName, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "读取命名空间 {Namespace} 失败", namespaceName);
            throw new VolumeDependencyException("Kubernetes 暂不可用");
        }

        if (namespaceInfo is null ||
            !namespaceInfo.Labels.TryGetValue(VolumeLabels.Managed, out var managed) ||
            managed != "true")
            return InvalidAll(request, "命名空间未标记为受管");

        IReadOnlyList<ObservedPvc> pvcs;
        try
        {
            pvcs = await kube.ListPvcsAsync(namespaceName, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "列出命名空间 {Namespace} 的 PVC 失败", namespaceName);
            throw new VolumeDependencyException("Kubernetes 暂不可用");
        }

        var pvcIndex = pvcs.ToDictionary(pvc => pvc.ClaimName, StringComparer.Ordinal);
        var claimNames = request.Volumes
            .Select(item => item?.ClaimName?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rowsByClaim = await db.Volumes
            .Where(volume => volume.Namespace == namespaceName && claimNames.Contains(volume.ClaimName))
            .ToDictionaryAsync(volume => volume.ClaimName, StringComparer.Ordinal, ct);
        // owner 唯一性按租户跨命名空间判定，避免同一 owner 在不同命名空间产生第二行台账。
        var tenantRows = await db.Volumes
            .Where(volume => volume.TenantId == tenantId)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var results = new List<VolumeRegistrationResult>();
        var toPatch = new List<VolumeEntity>();
        var seenClaims = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in request.Volumes)
        {
            var claimName = item?.ClaimName?.Trim() ?? "";
            if (item is null)
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Invalid, null, NullItemMessage));
                continue;
            }

            var validation = VolumeValidation.ValidateRegistration(item);
            if (validation is not null)
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Invalid, null, validation));
                continue;
            }

            if (!seenClaims.Add(claimName))
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Invalid, null, "请求中存在重复的 claimName"));
                continue;
            }

            if (!pvcIndex.TryGetValue(claimName, out var pvc))
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.NotFound, null, "PVC 不存在"));
                continue;
            }

            var ownerKind = item.OwnerKind.Trim();
            var ownerId = item.OwnerId.Trim();
            var lifecycle = item.Lifecycle.Trim();
            var byClaim = rowsByClaim.GetValueOrDefault(claimName);

            if (byClaim is not null &&
                (byClaim.TenantId != tenantId ||
                 byClaim.OwnerKind != ownerKind ||
                 byClaim.OwnerId != ownerId))
            {
                await AddConflictAsync(byClaim, actor, now, "卷已被其他租户或 owner 注册", ct);
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Conflict, byClaim.Id,
                    "卷已被其他租户或 owner 注册"));
                continue;
            }

            var ownerRow = tenantRows.FirstOrDefault(volume =>
                (volume.Namespace != namespaceName || volume.ClaimName != claimName) &&
                volume.OwnerKind == ownerKind &&
                volume.OwnerId == ownerId);
            if (ownerRow is not null)
            {
                await AddConflictAsync(ownerRow, actor, now, "owner 已绑定其他 PVC", ct);
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Conflict, ownerRow.Id,
                    "owner 已绑定其他 PVC"));
                continue;
            }

            if (byClaim is not null)
            {
                if (byClaim.Lifecycle != lifecycle)
                {
                    await AddConflictAsync(byClaim, actor, now, "lifecycle 与已有台账不一致", ct);
                    results.Add(new VolumeRegistrationResult(
                        claimName, RegistrationStatuses.Conflict, byClaim.Id,
                        "lifecycle 与已有台账不一致"));
                    continue;
                }

                var reactivated =
                    byClaim.State is VolumeStates.Missing or VolumeStates.Unmanaged;
                byClaim.Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax);
                byClaim.MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax);
                byClaim.LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax);
                byClaim.RegisteredUid = pvc.Uid;
                byClaim.State = VolumeStates.Active;
                byClaim.StateReason = null;
                byClaim.MissingSince = null;
                byClaim.LabelState = VolumeLabelStates.Pending;
                byClaim.SetViolations([]);
                byClaim.UpdatedAt = now;
                AddEvent(byClaim,
                    reactivated ? VolumeEventTypes.Reactivated : VolumeEventTypes.Updated,
                    actor, now);
                toPatch.Add(byClaim);
                results.Add(new VolumeRegistrationResult(
                    claimName,
                    reactivated ? RegistrationStatuses.Reactivated : RegistrationStatuses.Updated,
                    byClaim.Id, null));
                continue;
            }

            var volume = new VolumeEntity
            {
                Id = Guid.CreateVersion7(),
                Namespace = namespaceName,
                ClaimName = claimName,
                TenantId = tenantId,
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
            db.Volumes.Add(volume);
            rowsByClaim[claimName] = volume;
            tenantRows.Add(volume);
            AddEvent(volume, VolumeEventTypes.Registered, actor, now);
            toPatch.Add(volume);
            results.Add(new VolumeRegistrationResult(
                claimName, RegistrationStatuses.Registered, volume.Id, null));
        }

        await db.SaveChangesAsync(ct);

        foreach (var volume in toPatch.DistinctBy(entity => entity.Id))
        {
            var write = await labels.ApplyAllAsync(volume, ct);
            if (write.Success)
            {
                volume.LabelState = VolumeLabelStates.Applied;
                AddEvent(volume, VolumeEventTypes.LabelApplied, actor, time.GetUtcNow());
                continue;
            }

            for (var index = 0; index < results.Count; index++)
            {
                if (results[index].VolumeId != volume.Id)
                    continue;
                results[index] = results[index] with
                {
                    Status = RegistrationStatuses.LabelPending,
                    Message = write.Error,
                };
            }
            AddEvent(volume, VolumeEventTypes.LabelPatchFailed, actor, time.GetUtcNow(),
                write.Error);
        }

        await db.SaveChangesAsync(ct);
        return new VolumeRegistrationResponse(results);
    }

    private VolumeRegistrationResponse InvalidAll(
        VolumeRegistrationRequest request, string message) => new(
        request.Volumes.Select(item => new VolumeRegistrationResult(
                item?.ClaimName?.Trim() ?? "",
                RegistrationStatuses.Invalid,
                null,
                item is null ? NullItemMessage : message))
            .ToArray());

    private Task AddConflictAsync(
        VolumeEntity volume,
        string actor,
        DateTimeOffset now,
        string reason,
        CancellationToken ct)
    {
        AddEvent(volume, VolumeEventTypes.RegistrationConflict, actor, now, reason);
        return Task.CompletedTask;
    }

    private void AddEvent(
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
}
