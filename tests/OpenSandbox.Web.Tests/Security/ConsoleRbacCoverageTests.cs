using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenSandbox.Web.Host.Security;
using OpenSandbox.Web.Tests.Endpoints;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

/// <summary>
/// Static analysis of the real endpoint table: every registered API route must be
/// reachable through <see cref="ConsoleRbac"/> instead of falling into the Deny bucket.
/// </summary>
public sealed class ConsoleRbacCoverageTests
{
    private static readonly Regex ParameterSegment = new(@"\{[^}]*\}", RegexOptions.Compiled);

    [Fact]
    public void EveryRegisteredApiRoute_IsCoveredByRbac()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient(); // 触发主机构建，使端点表可用

        var routes = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(ToCases)
            .ToList();

        // 发现本身不能为空，否则下面的断言会静默通过。
        Assert.NotEmpty(routes);
        Assert.Contains(routes, route => route.Path == "/api/v1/console/me" && route.Method == "GET");

        foreach (var (path, method) in routes)
        {
            var decision = ConsoleRbac.Decide(new PathString(path), method);

            Assert.False(
                decision.Action == RbacAction.Deny,
                $"{method} {path} falls into RBAC Deny; register it in ConsoleRbac.Decide.");

            if (path.StartsWith("/api/v1/tenant", StringComparison.Ordinal))
                Assert.Equal(RbacAction.Skip, decision.Action);
        }
    }

    private static IEnumerable<(string Path, string Method)> ToCases(RouteEndpoint endpoint)
    {
        // RoutePattern.RawText 在本版本带前导斜杠（如 "/api/v1/health"），两种形式都接受。
        var rawText = endpoint.RoutePattern.RawText?.TrimStart('/');
        if (rawText is null || !rawText.StartsWith("api/", StringComparison.Ordinal))
            yield break;

        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        Assert.NotNull(methods);
        var path = "/" + ParameterSegment.Replace(rawText, "x");

        foreach (var method in methods!)
            yield return (path, method);
    }
}
