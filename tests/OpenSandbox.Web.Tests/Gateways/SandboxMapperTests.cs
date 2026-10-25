using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;
using Xunit;
using Models = OpenSandbox.Models;

namespace OpenSandbox.Web.Tests.Gateways;

public sealed class SandboxMapperTests
{
    [Fact]
    public void ToDto_MapsSandboxInfo()
    {
        var info = new Models.SandboxInfo
        {
            Id = "sb-1",
            Image = new Models.ImageSpec { Uri = "ubuntu:24.04" },
            Status = new Models.SandboxStatus { State = "Running" },
            Entrypoint = ["/bin/sh"],
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var dto = SandboxMapper.ToDto(info);

        Assert.Equal("sb-1", dto.Id);
        Assert.Equal("ubuntu:24.04", dto.Image.Uri);
        Assert.Equal("Running", dto.Status.State);
        Assert.Equal("/bin/sh", Assert.Single(dto.Entrypoint));
    }

    [Fact]
    public void ToSdk_MapsNetworkPolicyAndVolumes()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("ubuntu:24.04"),
            300,
            new Dictionary<string, string> { ["cpu"] = "500m" },
            ["/bin/sh"],
            null,
            null,
            new NetworkPolicyDto("deny", [new NetworkRuleDto("allow", "10.0.0.0/8")]),
            [new VolumeDto("data", "/data", false, null, "/tmp/data", null)],
            null);

        var sdk = SandboxMapper.ToSdk(request);

        Assert.Equal(Models.NetworkRuleAction.Deny, sdk.NetworkPolicy!.DefaultAction);
        Assert.Equal(Models.NetworkRuleAction.Allow, sdk.NetworkPolicy.Egress![0].Action);
        Assert.Equal("/tmp/data", sdk.Volumes![0].Host!.Path);
    }

    [Fact]
    public void ToSdk_MissingImageUri_ThrowsInvalidArgument()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("  "),
            null,
            null,
            ["/bin/sh"],
            null, null, null, null, null);

        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(() => SandboxMapper.ToSdk(request));

        Assert.Contains("image.uri", exception.Message);
    }

    [Fact]
    public void ToSdk_MapsExtensionsToObjectDictionary()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("ubuntu:24.04"),
            null,
            null,
            ["/bin/sh"],
            Extensions: new Dictionary<string, string> { ["gpu"] = "true" });

        var sdk = SandboxMapper.ToSdk(request);

        Assert.Equal("true", sdk.Extensions!["gpu"]);
    }

    [Fact]
    public void ToSdk_InvalidDefaultAction_ThrowsInvalidArgument()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("ubuntu:24.04"),
            null,
            null,
            ["/bin/sh"],
            NetworkPolicy: new NetworkPolicyDto("block", null));

        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(() => SandboxMapper.ToSdk(request));

        Assert.Contains("defaultAction", exception.Message);
    }

    [Fact]
    public void ToSdk_InvalidEgressAction_ThrowsInvalidArgument()
    {
        var request = new CreateSandboxRequest(
            new ImageRefDto("ubuntu:24.04"),
            null,
            null,
            ["/bin/sh"],
            NetworkPolicy: new NetworkPolicyDto(null, [new NetworkRuleDto("block", "10.0.0.0/8")]));

        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(() => SandboxMapper.ToSdk(request));

        Assert.Contains("egress.action", exception.Message);
    }

    [Fact]
    public void ToCreateResponse_CopiesMetadataIntoDictionary()
    {
        var response = new Models.CreateSandboxResponse
        {
            Id = "sb-2",
            Status = new Models.SandboxStatus { State = "Pending" },
            Metadata = new Dictionary<string, string> { ["owner"] = "demo" },
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Entrypoint = ["/bin/sh"],
        };

        var dto = SandboxMapper.ToCreateResponse(response);

        Assert.NotNull(dto.Metadata);
        Assert.IsType<Dictionary<string, string>>(dto.Metadata);
        Assert.Equal("demo", dto.Metadata!["owner"]);
    }

    [Fact]
    public void ToDto_MissingImage_ProducesEmptyImageRef()
    {
        var info = new Models.SandboxInfo
        {
            Id = "sb-3",
            Status = new Models.SandboxStatus { State = "Running" },
            Entrypoint = [],
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var dto = SandboxMapper.ToDto(info);

        Assert.Equal(string.Empty, dto.Image.Uri);
        Assert.Null(dto.Image.Auth);
    }

    [Fact]
    public void ToDto_NullImageAuthCredentials_MapToEmptyStrings()
    {
        var info = new Models.SandboxInfo
        {
            Id = "sb-4",
            Image = new Models.ImageSpec { Uri = "ubuntu:24.04", Auth = new Models.ImageAuth() },
            Status = new Models.SandboxStatus { State = "Running" },
            Entrypoint = [],
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var dto = SandboxMapper.ToDto(info);

        Assert.NotNull(dto.Image.Auth);
        Assert.Equal(string.Empty, dto.Image.Auth!.Username);
        Assert.Equal(string.Empty, dto.Image.Auth.Password);
    }

    [Fact]
    public void ExpiresAtToSeconds_Null_ThrowsInvalidArgument()
    {
        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(
            () => SandboxMapper.ExpiresAtToSeconds(null, DateTimeOffset.UtcNow));

        Assert.Contains("expiresAt", exception.Message);
    }

    [Fact]
    public void ExpiresAtToSeconds_Unparseable_ThrowsInvalidArgument()
    {
        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(
            () => SandboxMapper.ExpiresAtToSeconds("not-a-date", DateTimeOffset.UtcNow));

        Assert.Contains("expiresAt", exception.Message);
    }

    [Fact]
    public void ExpiresAtToSeconds_Past_ThrowsInvalidArgument()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<OpenSandbox.Core.InvalidArgumentException>(
            () => SandboxMapper.ExpiresAtToSeconds(utcNow.AddSeconds(-1).ToString("O"), utcNow));

        Assert.Contains("future", exception.Message);
    }

    [Fact]
    public void ExpiresAtToSeconds_Future_ReturnsCeilingSeconds()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var seconds = SandboxMapper.ExpiresAtToSeconds(utcNow.AddSeconds(300).ToString("O"), utcNow);

        Assert.Equal(300, seconds);
    }

    [Fact]
    public void ExpiresAtToSeconds_SubSecondFuture_ReturnsOneSecond()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var seconds = SandboxMapper.ExpiresAtToSeconds(utcNow.AddSeconds(0.4).ToString("O"), utcNow);

        Assert.Equal(1, seconds);
    }
}
