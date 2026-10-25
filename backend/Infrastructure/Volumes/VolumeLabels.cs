using System.Text.RegularExpressions;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeLabels
{
    public const string Managed = "opensandbox.io/volume-managed";
    public const string Lifecycle = "opensandbox.io/volume-lifecycle";
    public const string Tenant = "opensandbox.io/volume-tenant";
    public const string Owner = "opensandbox.io/volume-owner";
    public const string ContractVersion = "opensandbox.io/volume-contract-version";
    public const string ContractVersionValue = "1";

    private static readonly Regex InvalidOwnerCharacters =
        new("[^A-Za-z0-9_.-]", RegexOptions.Compiled);

    private static readonly Regex ValidLabelValue =
        new("^[A-Za-z0-9]([-A-Za-z0-9_.]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    public static string? NormalizeOwner(string ownerKind, string ownerId)
    {
        var raw = $"{ownerKind}-{ownerId}".Trim();
        if (raw.Length == 0) return null;

        var normalized = InvalidOwnerCharacters.Replace(raw, "-");
        return normalized.Length <= 63 && ValidLabelValue.IsMatch(normalized)
            ? normalized
            : null;
    }
}
