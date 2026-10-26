using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record TenantIdentity(string TenantId, string DisplayName);

public sealed class ApiKeyAuthenticator(VolumeDbContext db, TimeProvider time)
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
            await db.TenantApiKeys
                .Where(entity => entity.Id == key.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(entity => entity.LastUsedAt, now), ct);
        }

        return new TenantIdentity(key.TenantId, key.Tenant.DisplayName);
    }
}
