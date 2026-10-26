using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ApiKeyAuthenticatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, string RawKey)> SeedAsync(
        string status = "active", bool revoked = false)
    {
        var test = new TestDb();
        var tenant = await test.Db.Tenants.SingleAsync(t => t.Id == "goodcrew");
        tenant.Status = status;
        var raw = ApiKeys.Create();
        test.Db.TenantApiKeys.Add(new TenantApiKeyEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = "goodcrew",
            Name = "default",
            KeyHash = ApiKeys.Hash(raw),
            Prefix = ApiKeys.Prefix(raw),
            CreatedAt = Now,
            RevokedAt = revoked ? Now : null,
        });
        await test.Db.SaveChangesAsync();
        return (test, raw);
    }

    [Fact]
    public async Task ValidKey_ReturnsTenantIdentity()
    {
        var (test, raw) = await SeedAsync();
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        var identity = await authenticator.AuthenticateAsync(raw, CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("goodcrew", identity!.TenantId);
        var stored = await test.Db.TenantApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(Now, stored.LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    public async Task MissingOrUnknownKey_ReturnsNull(string? raw)
    {
        var (test, _) = await SeedAsync();
        using var __ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task RevokedKey_ReturnsNull()
    {
        var (test, raw) = await SeedAsync(revoked: true);
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledTenant_ReturnsNull()
    {
        var (test, raw) = await SeedAsync(status: "disabled");
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }
}
