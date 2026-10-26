using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class TenantServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static TenantService Service(TestDb test) =>
        new(test.Db, new FixedTimeProvider(Now));

    [Fact]
    public async Task Create_AddsTenantAndNamespaces()
    {
        using var test = new TestDb();

        var created = await Service(test).CreateAsync(
            new CreateTenantRequest("team-a", "Team A", ["team-a-ns"]), CancellationToken.None);

        Assert.Equal("team-a", created.Id);
        Assert.Equal(["team-a-ns"], created.Namespaces);
        Assert.True(await test.Db.TenantNamespaces.AnyAsync(n => n.Namespace == "team-a-ns"));
    }

    [Fact]
    public async Task Create_DuplicateId_ThrowsConflict()
    {
        using var test = new TestDb();

        await Assert.ThrowsAsync<VolumeConflictException>(() => Service(test).CreateAsync(
            new CreateTenantRequest("goodcrew", "Again", ["ns-1"]), CancellationToken.None));
    }

    [Fact]
    public async Task Create_NamespaceAlreadyAssigned_ThrowsConflict()
    {
        using var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox", TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<VolumeConflictException>(() => Service(test).CreateAsync(
            new CreateTenantRequest("team-b", "Team B", ["opensandbox"]), CancellationToken.None));
    }

    [Theory]
    [InlineData("Team")]
    [InlineData("team_b")]
    [InlineData("")]
    public async Task Create_InvalidTenantId_ThrowsValidation(string id)
    {
        using var test = new TestDb();

        await Assert.ThrowsAsync<VolumeValidationException>(() => Service(test).CreateAsync(
            new CreateTenantRequest(id, "Team", ["ns-1"]), CancellationToken.None));
    }

    [Fact]
    public async Task Update_ReplacesNamespacesAndKeepsKeys()
    {
        using var test = new TestDb();
        var service = Service(test);
        await service.CreateAsync(
            new CreateTenantRequest("team-a", "Team A", ["ns-a", "ns-b"]), CancellationToken.None);
        var key = await service.CreateKeyAsync(
            "team-a", new CreateTenantKeyRequest("ci"), CancellationToken.None);

        var updated = await service.UpdateAsync(
            "team-a", new UpdateTenantRequest("Team A2", "active", ["ns-b", "ns-c"]),
            CancellationToken.None);

        Assert.Equal("Team A2", updated.DisplayName);
        Assert.Equal(["ns-b", "ns-c"], updated.Namespaces.OrderBy(x => x));
        Assert.Equal(2, await test.Db.TenantNamespaces.CountAsync(n => n.TenantId == "team-a"));
        Assert.True(await test.Db.TenantApiKeys.AnyAsync(k => k.Id == key.Id));
    }

    [Fact]
    public async Task Update_DisabledStatus_IsPersisted()
    {
        using var test = new TestDb();

        var updated = await Service(test).UpdateAsync(
            "goodcrew", new UpdateTenantRequest(null, "disabled", null), CancellationToken.None);

        Assert.Equal("disabled", updated.Status);
    }

    [Fact]
    public async Task CreateKey_ReturnsPlaintextOnceAndStoresHash()
    {
        using var test = new TestDb();
        var service = Service(test);

        var created = await service.CreateKeyAsync(
            "goodcrew", new CreateTenantKeyRequest("ci"), CancellationToken.None);

        Assert.Equal(43, created.Key.Length);
        Assert.Equal(created.Key[..8], created.Prefix);
        var stored = await test.Db.TenantApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(ApiKeys.Hash(created.Key), stored.KeyHash);
        Assert.DoesNotContain(created.Key, Convert.ToBase64String(stored.KeyHash));

        var listed = await service.ListKeysAsync("goodcrew", CancellationToken.None);
        Assert.Equal(created.Key[..8], listed[0].Prefix);
    }

    [Fact]
    public async Task ListKeys_NewestFirst()
    {
        using var test = new TestDb();
        var first = await new TenantService(test.Db, new FixedTimeProvider(Now)).CreateKeyAsync(
            "goodcrew", new CreateTenantKeyRequest("first"), CancellationToken.None);
        var second = await new TenantService(test.Db, new FixedTimeProvider(Now.AddMinutes(1)))
            .CreateKeyAsync("goodcrew", new CreateTenantKeyRequest("second"), CancellationToken.None);

        var listed = await Service(test).ListKeysAsync("goodcrew", CancellationToken.None);

        Assert.Equal(second.Id, listed[0].Id);
        Assert.Equal(first.Id, listed[1].Id);
    }

    [Fact]
    public async Task RevokeKey_IsIdempotentAndScopedToTenant()
    {
        using var test = new TestDb();
        var service = Service(test);
        var created = await service.CreateKeyAsync(
            "goodcrew", new CreateTenantKeyRequest("ci"), CancellationToken.None);

        await service.RevokeKeyAsync("goodcrew", created.Id, CancellationToken.None);
        await service.RevokeKeyAsync("goodcrew", created.Id, CancellationToken.None);

        Assert.NotNull((await test.Db.TenantApiKeys.AsNoTracking().SingleAsync()).RevokedAt);
        await Assert.ThrowsAsync<VolumeNotFoundException>(() =>
            service.RevokeKeyAsync("other", created.Id, CancellationToken.None));
    }
}
