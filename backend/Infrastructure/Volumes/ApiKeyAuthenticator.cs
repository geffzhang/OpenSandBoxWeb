using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record TenantIdentity(string TenantId, string DisplayName);

public sealed class ApiKeyAuthenticator(
    VolumeDbContext db,
    TimeProvider time,
    ILogger<ApiKeyAuthenticator> logger)
{
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    public async Task<TenantIdentity?> AuthenticateAsync(string? rawKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var hash = ApiKeys.Hash(rawKey);
        var key = await db.TenantApiKeys
            .AsNoTracking()
            .Include(entity => entity.Tenant)
            .SingleOrDefaultAsync(entity =>
                entity.KeyHash == hash &&
                entity.RevokedAt == null &&
                entity.Tenant.Status == "active", ct);

        if (key is null)
            return null;

        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt >= LastUsedThrottle)
        {
            try
            {
                await db.TenantApiKeys
                    .Where(entity => entity.Id == key.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(entity => entity.LastUsedAt, now), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 记账失败不能影响认证结果，否则有效 Key 会被短暂拒绝。
                logger.LogWarning(exception, "更新租户 Key {KeyId} 的 LastUsedAt 失败", key.Id);
            }
        }

        return new TenantIdentity(key.TenantId, key.Tenant.DisplayName);
    }
}
