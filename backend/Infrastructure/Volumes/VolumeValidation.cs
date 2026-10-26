using System.Text.RegularExpressions;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeValidation
{
    public const int OwnerKindMax = 32;
    public const int OwnerIdMax = 128;
    public const int RoleMax = 32;
    public const int MountPathMax = 128;
    public const int SandboxIdMax = 128;
    public const int ReasonMax = 512;

    private static readonly Regex TenantIdPattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly Regex DnsLabelPattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly Regex ClaimNamePattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$",
            RegexOptions.Compiled);

    public static string? TenantId(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 63 } && TenantIdPattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? Namespace(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 63 } && DnsLabelPattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? ClaimName(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 253 } && ClaimNamePattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? Text(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max)
            return null;
        return trimmed;
    }

    public static string? ValidateRegistration(VolumeRegistrationItem item)
    {
        if (ClaimName(item.ClaimName) is null)
            return "claimName 不合法";
        if (Text(item.OwnerKind, OwnerKindMax) is null)
            return "ownerKind 为空或超过 32 字符";
        if (Text(item.OwnerId, OwnerIdMax) is null)
            return "ownerId 为空或超过 128 字符";
        if (!string.IsNullOrWhiteSpace(item.Role) && Text(item.Role, RoleMax) is null)
            return "role 超过 32 字符";
        if (!string.IsNullOrWhiteSpace(item.MountPath) && Text(item.MountPath, MountPathMax) is null)
            return "mountPath 超过 128 字符";
        if (!VolumeLifecycle.IsValid(item.Lifecycle?.Trim()))
            return "lifecycle 必须为 business 或 ephemeral";
        if (!string.IsNullOrWhiteSpace(item.SandboxId) && Text(item.SandboxId, SandboxIdMax) is null)
            return "sandboxId 超过 128 字符";
        return null;
    }

    public static string? ValidateImportItem(VolumeImportItem item)
    {
        if (TenantId(item.Tenant) is null)
            return "tenant 不合法";
        if (Namespace(item.Namespace) is null)
            return "namespace 不合法";
        var registration = ValidateRegistration(new VolumeRegistrationItem(
            item.ClaimName, item.OwnerKind, item.OwnerId,
            item.Role, item.MountPath, item.Lifecycle, item.SandboxId));
        return registration;
    }
}
