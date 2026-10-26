using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeClassifierTests
{
    [Theory]
    [InlineData(null, VolumeKind.Unlabeled)]
    [InlineData("", VolumeKind.Unlabeled)]
    [InlineData("   ", VolumeKind.Unlabeled)]
    [InlineData("ephemeral", VolumeKind.Ephemeral)]
    [InlineData(" ephemeral ", VolumeKind.Ephemeral)]
    [InlineData("business", VolumeKind.Business)]
    [InlineData("Business", VolumeKind.Business)]
    [InlineData("busines", VolumeKind.Business)]
    [InlineData("EPHEMERAL", VolumeKind.Business)]
    [InlineData("unknown", VolumeKind.Business)]
    public void Classify_MatchesContract(string? label, VolumeKind expected)
        => Assert.Equal(expected, VolumeClassifier.Classify(label));

    [Theory]
    [InlineData(VolumeKind.Unlabeled, "unlabeled")]
    [InlineData(VolumeKind.Ephemeral, "ephemeral")]
    [InlineData(VolumeKind.Business, "business")]
    public void ToApiValue_IsStable(VolumeKind kind, string expected)
        => Assert.Equal(expected, VolumeClassifier.ToApiValue(kind));
}
