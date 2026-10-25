using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;
using Xunit;

namespace OpenSandbox.Web.Tests.Security;

public sealed class ConsoleAuthTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    [Fact]
    public void AddConsoleAuth_RequiresAuthorityAndClientId()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddConsoleAuth(Config()));
    }

    [Fact]
    public void AddConsoleAuth_ConfiguresJwtBearer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConsoleAuth(Config(
            ("Auth:Authority", "https://keycloak.example/realms/dev/"),
            ("Auth:ClientId", "opensandbox-web"),
            ("Auth:RequireHttpsMetadata", "false")));

        using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal("https://keycloak.example/realms/dev", jwt.Authority);
        Assert.False(jwt.RequireHttpsMetadata);
        Assert.False(jwt.MapInboundClaims);
        Assert.True(jwt.TokenValidationParameters.ValidateIssuer);
        Assert.Equal("https://keycloak.example/realms/dev", jwt.TokenValidationParameters.ValidIssuer);
        Assert.False(jwt.TokenValidationParameters.ValidateAudience);
        Assert.True(jwt.TokenValidationParameters.ValidateLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), jwt.TokenValidationParameters.ClockSkew);
        Assert.Equal("preferred_username", jwt.TokenValidationParameters.NameClaimType);
    }

    [Theory]
    [InlineData("opensandbox-web", true)]
    [InlineData("another-client", false)]
    [InlineData(null, false)]
    public void AzpMatches_RequiresExactClient(string? azp, bool expected)
    {
        var identity = new ClaimsIdentity("Test");
        if (azp is not null)
            identity.AddClaim(new Claim("azp", azp));
        var principal = new ClaimsPrincipal(identity);

        Assert.Equal(expected, ConsoleAuth.AzpMatches(principal, "opensandbox-web"));
    }

    [Fact]
    public void AzpMatches_MissingPrincipal_IsFalse()
        => Assert.False(ConsoleAuth.AzpMatches(null, "opensandbox-web"));
}
