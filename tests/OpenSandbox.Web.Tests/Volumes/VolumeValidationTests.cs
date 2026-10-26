using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeValidationTests
{
    [Theory]
    [InlineData("goodcrew", "goodcrew")]
    [InlineData(" goodcrew ", "goodcrew")]
    [InlineData("team-1", "team-1")]
    [InlineData("Team", null)]
    [InlineData("team_1", null)]
    [InlineData("-team", null)]
    [InlineData("team-", null)]
    [InlineData("", null)]
    public void TenantId_MatchesSlug(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.TenantId(input));

    [Theory]
    [InlineData("opensandbox", "opensandbox")]
    [InlineData("team-a", "team-a")]
    [InlineData("Team", null)]
    public void Namespace_MatchesDnsLabel(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.Namespace(input));

    [Fact]
    public void Namespace_RejectsOverlongLabel()
        => Assert.Null(VolumeValidation.Namespace(new string('a', 64)));

    [Theory]
    [InlineData("pvc-1", "pvc-1")]
    [InlineData("app.data", "app.data")]
    [InlineData("app..data", null)]
    [InlineData("-pvc", null)]
    public void ClaimName_MatchesDnsSubdomain(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.ClaimName(input));

    private static VolumeRegistrationItem Item(
        string claimName = "pvc-1",
        string ownerKind = "employee",
        string ownerId = "3f2a",
        string? role = "meta",
        string? mountPath = "/workspace",
        string lifecycle = "business",
        string? sandboxId = "sb-1")
        => new(claimName, ownerKind, ownerId, role, mountPath, lifecycle, sandboxId);

    [Fact]
    public void Registration_ValidItem_ReturnsNull()
        => Assert.Null(VolumeValidation.ValidateRegistration(Item()));

    [Theory]
    [InlineData("", "employee", "3f2a", "business")]
    [InlineData("pvc-1", "", "3f2a", "business")]
    [InlineData("pvc-1", "employee", "", "business")]
    [InlineData("pvc-1", "employee", "3f2a", "archive")]
    [InlineData("pvc-1", "employee", "3f2a", "")]
    public void Registration_InvalidItem_ReturnsMessage(
        string claimName, string ownerKind, string ownerId, string lifecycle)
        => Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(claimName, ownerKind, ownerId, lifecycle: lifecycle)));

    [Fact]
    public void Registration_FieldLimitsAreEnforced()
    {
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(ownerKind: new string('a', 33))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(ownerId: new string('a', 129))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(role: new string('a', 33))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(mountPath: "/" + new string('a', 128))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(sandboxId: new string('a', 129))));
    }

    [Fact]
    public void Registration_EmptyOptionalFields_AreAllowed()
        => Assert.Null(VolumeValidation.ValidateRegistration(
            Item(role: "", mountPath: "", sandboxId: "")));

    [Fact]
    public void Import_ValidItem_ReturnsNull()
    {
        var item = new VolumeImportItem(
            "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
            "meta", "/workspace", "business", "sb-1");
        Assert.Null(VolumeValidation.ValidateImportItem(item));
    }

    [Theory]
    [InlineData("Team", "opensandbox", "pvc-1")]
    [InlineData("goodcrew", "Team", "pvc-1")]
    [InlineData("goodcrew", "opensandbox", "-pvc")]
    [InlineData("goodcrew", "opensandbox", "pvc-1", "archive")]
    public void Import_InvalidItem_ReturnsMessage(
        string tenant, string namespaceName, string claimName, string lifecycle = "business")
    {
        var item = new VolumeImportItem(
            tenant, namespaceName, claimName, "employee", "3f2a",
            "meta", "/workspace", lifecycle, "sb-1");
        Assert.NotNull(VolumeValidation.ValidateImportItem(item));
    }
}
