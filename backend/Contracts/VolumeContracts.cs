namespace OpenSandbox.Web.Contracts;

public static class RegistrationStatuses
{
    public const string Registered = "registered";
    public const string Updated = "updated";
    public const string Reactivated = "reactivated";
    public const string Conflict = "conflict";
    public const string NotFound = "not-found";
    public const string Invalid = "invalid";
    public const string LabelPending = "label-pending";
}

public sealed record VolumeRegistrationRequest(
    string Namespace,
    IReadOnlyList<VolumeRegistrationItem> Volumes);

public sealed record VolumeRegistrationItem(
    string ClaimName,
    string OwnerKind,
    string OwnerId,
    string? Role,
    string? MountPath,
    string Lifecycle,
    string? SandboxId);

public sealed record VolumeRegistrationResult(
    string ClaimName,
    string Status,
    Guid? VolumeId,
    string? Message);

public sealed record VolumeRegistrationResponse(
    IReadOnlyList<VolumeRegistrationResult> Results);

public sealed record VolumeLifecycleRequest(string Lifecycle, string? Reason);

public sealed record VolumeLifecycleResponse(string Status, string LabelState);

public sealed record VolumeListItemDto(
    Guid? Id,
    string Namespace,
    string ClaimName,
    string? Tenant,
    string? TenantName,
    string? OwnerKind,
    string? OwnerId,
    string? Role,
    string? MountPath,
    string? Lifecycle,
    string? State,
    string? StateReason,
    string Classification,
    string? LabelState,
    IReadOnlyList<string> Violations,
    string? RegisteredUid,
    string? ObservedUid,
    DateTimeOffset? RegisteredAt,
    DateTimeOffset? LastSeenAt,
    bool? Mounted,
    string? StorageClass,
    string? Capacity,
    DateTimeOffset? CreatedAt);

public sealed record VolumeListResponse(
    IReadOnlyList<VolumeListItemDto> Items,
    PaginationInfoDto Pagination);

public sealed record VolumeDetailDto(
    VolumeListItemDto Volume,
    ClusterVolumeDto? Observed,
    IReadOnlyList<VolumeEventDto> Events);

public sealed record ClusterVolumeDto(
    string Namespace,
    string ClaimName,
    string Uid,
    string Phase,
    string? StorageClass,
    string? RequestedStorage,
    string? Capacity,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionTimestamp,
    bool Mounted,
    IReadOnlyList<string> MountedBy,
    IReadOnlyDictionary<string, string> Labels,
    string? LifecycleLabel,
    string? TenantLabel,
    string? OwnerLabel,
    string? ContractVersionLabel,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record VolumeEventDto(
    Guid Id,
    Guid? VolumeId,
    string Namespace,
    string ClaimName,
    string? TenantId,
    string Type,
    string Actor,
    string? Reason,
    string DataJson,
    DateTimeOffset CreatedAt);

public sealed record VolumeReportCounts(
    int Observed,
    int Registered,
    int Unlabeled,
    int Unregistered,
    int Drift,
    int Missing,
    int Reappeared,
    int Unmanaged);

public sealed record VolumeCoverageDto(
    int Observed,
    int Labeled,
    int Registered,
    decimal LabeledPercent,
    decimal RegisteredPercent);

public sealed record VolumeDistributionItem(string Key, int Count);

public sealed record VolumeViolationDto(
    string Type,
    string Namespace,
    string ClaimName,
    string Message);

public sealed record ScanErrorDto(string Namespace, string Message);

public sealed record ScanRunDto(
    Guid Id,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanErrorDto> Errors);

public sealed record VolumeScanResponse(
    Guid ScanId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanErrorDto> Errors);

public sealed record VolumeReportDto(
    ScanRunDto? LastScan,
    VolumeReportCounts Counts,
    VolumeCoverageDto Coverage,
    IReadOnlyList<VolumeDistributionItem> ByTenant,
    IReadOnlyList<VolumeDistributionItem> ByNamespace,
    IReadOnlyList<VolumeViolationDto> Violations,
    bool Truncated);

public sealed record VolumeImportRequest(
    bool? DryRun,
    IReadOnlyList<VolumeImportItem> Items);

public sealed record VolumeImportItem(
    string Tenant,
    string Namespace,
    string ClaimName,
    string OwnerKind,
    string OwnerId,
    string? Role,
    string? MountPath,
    string Lifecycle,
    string? SandboxId);

public sealed record VolumeImportResult(
    string Namespace,
    string ClaimName,
    string Status,
    string? Message);

public sealed record VolumeImportResponse(
    bool DryRun,
    IReadOnlyList<VolumeImportResult> Results);

public sealed record VolumeTenantDto(
    string Id,
    string DisplayName,
    string Status,
    IReadOnlyList<string> Namespaces,
    int VolumeCount,
    int ProtectedCount,
    int EphemeralCount,
    int MissingCount,
    int KeyCount,
    DateTimeOffset CreatedAt);

public sealed record TenantListResponse(IReadOnlyList<VolumeTenantDto> Items);

public sealed record CreateTenantRequest(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Namespaces);

public sealed record UpdateTenantRequest(
    string? DisplayName,
    string? Status,
    IReadOnlyList<string>? Namespaces);

public sealed record TenantKeyDto(
    Guid Id,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record TenantKeyListResponse(IReadOnlyList<TenantKeyDto> Items);

public sealed record CreateTenantKeyRequest(string? Name);

public sealed record CreateTenantKeyResponse(Guid Id, string Key, string Prefix);
