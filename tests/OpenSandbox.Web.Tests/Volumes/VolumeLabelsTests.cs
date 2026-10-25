using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeLabelsTests
{
    [Fact]
    public void NormalizeOwner_JoinsKindAndId()
        => Assert.Equal("employee-3f2a9c1b7d4e", VolumeLabels.NormalizeOwner("employee", "3f2a9c1b7d4e"));

    [Fact]
    public void NormalizeOwner_ReplacesInvalidCharacters()
        => Assert.Equal("employee-3f2a-9c", VolumeLabels.NormalizeOwner("employee", "3f2a:9c"));

    [Fact]
    public void NormalizeOwner_KeepsDotsUnderscoresAndDashes()
        => Assert.Equal("employee-a.b_c-d", VolumeLabels.NormalizeOwner("employee", "a.b_c-d"));

    [Fact]
    public void NormalizeOwner_OmitsWhenLongerThan63()
        => Assert.Null(VolumeLabels.NormalizeOwner("employee", new string('a', 56)));

    [Theory]
    [InlineData("", "abc")]
    [InlineData("employee", "")]
    [InlineData("employee", "!!!")]
    public void NormalizeOwner_OmitsInvalidResults(string kind, string id)
        => Assert.Null(VolumeLabels.NormalizeOwner(kind, id));

    [Fact]
    public void ContractVersion_IsOne()
        => Assert.Equal("1", VolumeLabels.ContractVersionValue);

    [Fact]
    public void KeyNames_AreExact()
    {
        Assert.Equal("opensandbox.io/volume-managed", VolumeLabels.Managed);
        Assert.Equal("opensandbox.io/volume-lifecycle", VolumeLabels.Lifecycle);
        Assert.Equal("opensandbox.io/volume-tenant", VolumeLabels.Tenant);
        Assert.Equal("opensandbox.io/volume-owner", VolumeLabels.Owner);
        Assert.Equal("opensandbox.io/volume-contract-version", VolumeLabels.ContractVersion);
    }
}
