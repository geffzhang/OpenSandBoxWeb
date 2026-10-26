using System.Security.Claims;
using System.Text.Json;
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleRolesTests
{
    private const string ClientId = "opensandbox-web";

    private static ClaimsPrincipal PrincipalWithResourceAccess(object resourceAccess)
    {
        var json = JsonSerializer.Serialize(resourceAccess);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim("resource_access", json),
        ], "Test"));
    }

    [Fact]
    public void RolesOf_ReadsOnlyDedicatedClientRoles()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            ["account"] = new { roles = new[] { "sandbox:admin" } },
            [ClientId] = new { roles = new[] { "volume:viewer" } },
        });

        var roles = ConsoleRoles.RolesOf(principal, ClientId);

        Assert.Equal(new[] { "volume:viewer" }, roles);
    }

    [Fact]
    public void RolesOf_MissingResourceAccess_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_MalformedJson_ReturnsEmpty()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("resource_access", "{not json")], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_RolesNotArray_ReturnsEmpty()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            [ClientId] = new { roles = "sandbox:admin" },
        });

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void RolesOf_NonObjectResourceAccess_ReturnsEmpty(string resourceAccess)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("resource_access", resourceAccess)], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Theory]
    [InlineData("{\"opensandbox-web\": []}")]
    [InlineData("{\"opensandbox-web\": \"x\"}")]
    [InlineData("{\"opensandbox-web\": null}")]
    [InlineData("{\"opensandbox-web\": 42}")]
    public void RolesOf_NonObjectClientEntry_ReturnsEmpty(string resourceAccess)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("resource_access", resourceAccess)], "Test"));

        Assert.Empty(ConsoleRoles.RolesOf(principal, ClientId));
    }

    [Fact]
    public void RolesOf_DropsUnknownRoles()
    {
        var principal = PrincipalWithResourceAccess(new Dictionary<string, object>
        {
            [ClientId] = new { roles = new[] { "sandbox:admin", "realm-admin", "volume:viewer" } },
        });

        var roles = ConsoleRoles.RolesOf(principal, ClientId);

        Assert.True(roles.SetEquals(new[] { "sandbox:admin", "volume:viewer" }));
    }

    [Theory]
    [InlineData("sandbox:admin", "sandbox:viewer", true)]
    [InlineData("sandbox:viewer", "sandbox:admin", false)]
    [InlineData("volume:admin", "volume:viewer", true)]
    [InlineData("volume:viewer", "volume:admin", false)]
    [InlineData("sandbox:admin", "volume:viewer", false)]
    [InlineData("volume:admin", "sandbox:viewer", false)]
    [InlineData("sandbox:admin", "sandbox:admin", true)]
    public void Has_AppliesFacetHierarchy(string granted, string required, bool expected)
    {
        var roles = new HashSet<string> { granted };

        Assert.Equal(expected, ConsoleRoles.Has(roles, required));
    }
}
