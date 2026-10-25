using Microsoft.AspNetCore.Http;
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleRbacTests
{
    [Theory]
    [InlineData("/api/v1/health", "GET", RbacAction.Skip, null)]
    [InlineData("/healthz", "GET", RbacAction.Skip, null)]
    [InlineData("/readyz", "GET", RbacAction.Skip, null)]
    [InlineData("/dashboard", "GET", RbacAction.Skip, null)]
    [InlineData("/api/v1/tenant/volumes", "GET", RbacAction.Skip, null)]
    [InlineData("/api/v1/sandboxes", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes", "HEAD", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes", "POST", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1", "DELETE", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1/endpoints/8080", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxAdmin)]
    [InlineData("/api/v1/sandboxes/sb-1/metrics", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/sandboxes/sb-1/metrics/watch", "GET", RbacAction.RequireRole, ConsoleRoles.SandboxViewer)]
    [InlineData("/api/v1/volumes", "GET", RbacAction.RequireRole, ConsoleRoles.VolumeViewer)]
    [InlineData("/api/v1/volumes/vol-1", "PATCH", RbacAction.RequireRole, ConsoleRoles.VolumeAdmin)]
    [InlineData("/api/v1/volume-tenants", "GET", RbacAction.RequireRole, ConsoleRoles.VolumeViewer)]
    [InlineData("/api/v1/volume-tenants", "POST", RbacAction.RequireRole, ConsoleRoles.VolumeAdmin)]
    [InlineData("/api/v1/console/me", "GET", RbacAction.AnyAuthenticated, null)]
    [InlineData("/api/v1/unknown", "GET", RbacAction.Deny, null)]
    [InlineData("/api/v1/sandboxes", "OPTIONS", RbacAction.Skip, null)]
    public void Decide_MapsPathAndMethod(string path, string method, RbacAction action, string? role)
    {
        var decision = ConsoleRbac.Decide(new PathString(path), method);

        Assert.Equal(action, decision.Action);
        Assert.Equal(role, decision.Role);
    }
}
