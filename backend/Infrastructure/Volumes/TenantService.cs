using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class TenantService(VolumeDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<VolumeTenantDto>> ListAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Include(tenant => tenant.Namespaces)
            .OrderBy(tenant => tenant.Id)
            .ToListAsync(ct);
        var volumes = await db.Volumes.AsNoTracking().ToListAsync(ct);
        var keys = await db.TenantApiKeys.AsNoTracking()
            .Where(key => key.RevokedAt == null)
            .GroupBy(key => key.TenantId)
            .Select(group => new { TenantId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.TenantId, row => row.Count, ct);

        return tenants
            .Select(tenant => ToDto(
                tenant,
                volumes.Where(volume => volume.TenantId == tenant.Id).ToList(),
                keys.GetValueOrDefault(tenant.Id)))
            .ToArray();
    }

    public async Task<VolumeTenantDto> CreateAsync(
        CreateTenantRequest request, CancellationToken ct)
    {
        var id = VolumeValidation.TenantId(request.Id)
            ?? throw new VolumeValidationException("租户 ID 必须为小写 DNS 标签，最长 63 字符");
        var displayName = VolumeValidation.Text(request.DisplayName, 200)
            ?? throw new VolumeValidationException("显示名称不能为空且不超过 200 字符");
        var namespaces = NormalizeNamespaces(request.Namespaces);

        if (await db.Tenants.AnyAsync(tenant => tenant.Id == id, ct))
            throw new VolumeConflictException("租户已存在");

        var conflicts = await db.TenantNamespaces
            .Where(mapping => namespaces.Contains(mapping.Namespace))
            .Select(mapping => mapping.Namespace)
            .ToListAsync(ct);
        if (conflicts.Count > 0)
            throw new VolumeConflictException($"命名空间已分配给其他租户: {string.Join(", ", conflicts)}");

        var now = time.GetUtcNow();
        var tenant = new TenantEntity
        {
            Id = id,
            DisplayName = displayName,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
            Namespaces = namespaces.Select(namespaceName => new TenantNamespaceEntity
            {
                Namespace = namespaceName,
                TenantId = id,
            }).ToList(),
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<VolumeTenantDto> UpdateAsync(
        string tenantId, UpdateTenantRequest request, CancellationToken ct)
    {
        var tenant = await db.Tenants
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity => entity.Id == tenantId, ct)
            ?? throw new VolumeNotFoundException("租户不存在");

        if (request.DisplayName is not null)
        {
            tenant.DisplayName = VolumeValidation.Text(request.DisplayName, 200)
                ?? throw new VolumeValidationException("显示名称不能为空且不超过 200 字符");
        }

        if (request.Status is not null)
        {
            if (request.Status is not ("active" or "disabled"))
                throw new VolumeValidationException("status 必须为 active 或 disabled");
            tenant.Status = request.Status;
        }

        if (request.Namespaces is not null)
        {
            var next = NormalizeNamespaces(request.Namespaces);
            var conflicts = await db.TenantNamespaces
                .Where(mapping =>
                    next.Contains(mapping.Namespace) && mapping.TenantId != tenantId)
                .Select(mapping => mapping.Namespace)
                .ToListAsync(ct);
            if (conflicts.Count > 0)
                throw new VolumeConflictException($"命名空间已分配给其他租户: {string.Join(", ", conflicts)}");

            var existing = tenant.Namespaces.Select(mapping => mapping.Namespace).ToHashSet();
            db.TenantNamespaces.RemoveRange(
                tenant.Namespaces.Where(mapping => !next.Contains(mapping.Namespace)));
            foreach (var namespaceName in next.Where(name => !existing.Contains(name)))
            {
                tenant.Namespaces.Add(new TenantNamespaceEntity
                {
                    Namespace = namespaceName,
                    TenantId = tenantId,
                });
            }
        }

        tenant.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, ct);
    }

    public async Task<IReadOnlyList<TenantKeyDto>> ListKeysAsync(
        string tenantId, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        var keys = await db.TenantApiKeys.AsNoTracking()
            .Where(key => key.TenantId == tenantId)
            .Select(key => new TenantKeyDto(
                key.Id, key.Name, key.Prefix, key.CreatedAt, key.RevokedAt, key.LastUsedAt))
            .ToListAsync(ct);
        return keys.OrderByDescending(key => key.CreatedAt).ToArray();
    }

    public async Task<CreateTenantKeyResponse> CreateKeyAsync(
        string tenantId, CreateTenantKeyRequest request, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? "默认 Key"
            : VolumeValidation.Text(request.Name, 200)
              ?? throw new VolumeValidationException("Key 名称不超过 200 字符");

        var raw = ApiKeys.Create();
        var entity = new TenantApiKeyEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            KeyHash = ApiKeys.Hash(raw),
            Prefix = ApiKeys.Prefix(raw),
            CreatedAt = time.GetUtcNow(),
        };
        db.TenantApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        return new CreateTenantKeyResponse(entity.Id, raw, entity.Prefix);
    }

    public async Task RevokeKeyAsync(string tenantId, Guid keyId, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        var key = await db.TenantApiKeys.SingleOrDefaultAsync(
            entity => entity.Id == keyId && entity.TenantId == tenantId, ct)
            ?? throw new VolumeNotFoundException("Key 不存在");

        if (key.RevokedAt is null)
        {
            key.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<VolumeTenantDto> GetAsync(string tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity => entity.Id == tenantId, ct)
            ?? throw new VolumeNotFoundException("租户不存在");
        var volumes = await db.Volumes.AsNoTracking()
            .Where(volume => volume.TenantId == tenantId)
            .ToListAsync(ct);
        var keyCount = await db.TenantApiKeys.AsNoTracking()
            .CountAsync(key => key.TenantId == tenantId && key.RevokedAt == null, ct);
        return ToDto(tenant, volumes, keyCount);
    }

    private async Task EnsureTenantAsync(string tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
            throw new VolumeNotFoundException("租户不存在");
    }

    private static List<string> NormalizeNamespaces(IReadOnlyList<string>? namespaces)
    {
        if (namespaces is null)
            return [];

        var result = new List<string>();
        foreach (var value in namespaces)
        {
            var namespaceName = VolumeValidation.Namespace(value)
                ?? throw new VolumeValidationException($"命名空间不合法: {value}");
            if (!result.Contains(namespaceName, StringComparer.Ordinal))
                result.Add(namespaceName);
        }
        return result;
    }

    private static VolumeTenantDto ToDto(
        TenantEntity tenant,
        IReadOnlyList<VolumeEntity> volumes,
        int keyCount) => new(
        tenant.Id,
        tenant.DisplayName,
        tenant.Status,
        tenant.Namespaces.Select(mapping => mapping.Namespace).OrderBy(name => name).ToArray(),
        volumes.Count,
        volumes.Count(volume => volume.Lifecycle == VolumeLifecycle.Business),
        volumes.Count(volume => volume.Lifecycle == VolumeLifecycle.Ephemeral),
        volumes.Count(volume => volume.State == VolumeStates.Missing),
        keyCount,
        tenant.CreatedAt);
}
