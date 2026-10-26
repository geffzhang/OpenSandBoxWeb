using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class LifecycleService(
    VolumeDbContext db,
    VolumeLabelWriter labels,
    TimeProvider time,
    ILogger<LifecycleService> logger)
{
    public async Task<VolumeLifecycleResponse> ChangeAsync(
        string? tenantId,
        string namespaceName,
        string claimName,
        VolumeLifecycleRequest request,
        bool isAdmin,
        string actor,
        CancellationToken ct)
    {
        var lifecycle = request.Lifecycle?.Trim() ?? "";
        if (!VolumeLifecycle.IsValid(lifecycle))
            throw new VolumeValidationException("lifecycle 必须为 business 或 ephemeral");

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? null
            : request.Reason.Trim();
        if (reason is { Length: > VolumeValidation.ReasonMax })
            throw new VolumeValidationException("reason 不超过 512 字符");

        var volume = await db.Volumes.SingleOrDefaultAsync(entity =>
            entity.Namespace == namespaceName && entity.ClaimName == claimName, ct)
            ?? throw new VolumeNotFoundException("卷不存在");

        if (!isAdmin && (tenantId is null || volume.TenantId != tenantId))
            throw new VolumeNotFoundException("卷不存在");

        if (volume.Lifecycle == lifecycle)
            return new VolumeLifecycleResponse("unchanged", volume.LabelState);

        if (lifecycle == VolumeLifecycle.Ephemeral && reason is null)
            throw new VolumeValidationException("降级为 ephemeral 必须提供 reason");

        var now = time.GetUtcNow();
        var previous = volume.Lifecycle;
        volume.Lifecycle = lifecycle;
        volume.LabelState = VolumeLabelStates.Pending;
        volume.UpdatedAt = now;

        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = VolumeEventTypes.LifecycleChanged,
            Actor = actor,
            Reason = reason,
            DataJson = JsonColumn.Write(new { from = previous, to = lifecycle }),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        var write = await labels.ApplyLifecycleAsync(volume, lifecycle, ct);
        if (write.Success)
        {
            volume.LabelState = VolumeLabelStates.Applied;
            db.VolumeEvents.Add(new VolumeEventEntity
            {
                Id = Guid.CreateVersion7(),
                VolumeId = volume.Id,
                Namespace = volume.Namespace,
                ClaimName = volume.ClaimName,
                TenantId = volume.TenantId,
                Type = VolumeEventTypes.LabelApplied,
                Actor = actor,
                DataJson = "{}",
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            logger.LogWarning("卷 {Namespace}/{ClaimName} 标签写入失败: {Error}",
                volume.Namespace, volume.ClaimName, write.Error);
            db.VolumeEvents.Add(new VolumeEventEntity
            {
                Id = Guid.CreateVersion7(),
                VolumeId = volume.Id,
                Namespace = volume.Namespace,
                ClaimName = volume.ClaimName,
                TenantId = volume.TenantId,
                Type = VolumeEventTypes.LabelPatchFailed,
                Actor = actor,
                Reason = write.Error,
                DataJson = "{}",
                CreatedAt = time.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct);
        return new VolumeLifecycleResponse(
            write.Success ? "updated" : "label-pending", volume.LabelState);
    }
}
