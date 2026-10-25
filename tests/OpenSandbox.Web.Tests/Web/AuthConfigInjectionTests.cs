using OpenSandbox.Web.Host.Security;
using OpenSandbox.Web.Host.Web;
using Xunit;

namespace OpenSandbox.Web.Tests.Web;

public sealed class AuthConfigInjectionTests
{
    private static readonly ConsoleAuthOptions Options = new()
    {
        Authority = "https://keycloak.example/realms/dev",
        ClientId = "opensandbox-web",
    };

    [Fact]
    public void Inject_AddsAuthConfigBeforeHeadEnd()
    {
        var html = "<!doctype html><html><head><title>x</title></head><body></body></html>";

        var injected = AuthConfigInjection.Inject(html, Options);

        Assert.Contains(
            "<script>window.__OPENSANDBOX_AUTH__={\"authority\":\"https://keycloak.example/realms/dev\",\"clientId\":\"opensandbox-web\"};</script></head>",
            injected);
    }

    [Fact]
    public void Inject_EscapesHtmlSensitiveCharacters()
    {
        var options = new ConsoleAuthOptions
        {
            Authority = "https://x/</script><script>",
            ClientId = "c",
        };

        var injected = AuthConfigInjection.Inject("<head></head>", options);

        Assert.DoesNotContain("</script><script>", injected);
        Assert.Contains("\\u003C", injected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_ThrowsWhenHeadMissing()
    {
        Assert.Throws<InvalidOperationException>(
            () => AuthConfigInjection.Inject("<html></html>", Options));
    }
}
