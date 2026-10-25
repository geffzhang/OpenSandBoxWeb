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
}
