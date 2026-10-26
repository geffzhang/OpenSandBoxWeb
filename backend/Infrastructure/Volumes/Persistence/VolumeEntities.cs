namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public sealed class TenantEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<TenantNamespaceEntity> Namespaces { get; set; } = [];
    public List<TenantApiKeyEntity> ApiKeys { get; set; } = [];
}

public sealed class TenantNamespaceEntity
{
    public string Namespace { get; set; } = "";
    public string TenantId { get; set; } = "";
    public TenantEntity Tenant { get; set; } = null!;
}

public sealed class TenantApiKeyEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public string Prefix { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public TenantEntity Tenant { get; set; } = null!;
}

public sealed class VolumeEntity
{
    public Guid Id { get; set; }
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string OwnerKind { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string? Role { get; set; }
    public string? MountPath { get; set; }
    public string Lifecycle { get; set; } = VolumeLifecycle.Business;
    public string ContractVersion { get; set; } = VolumeLabels.ContractVersionValue;
    public string? LastSandboxId { get; set; }
    public string? RegisteredUid { get; set; }
    public string State { get; set; } = VolumeStates.Active;
    public string? StateReason { get; set; }
    public DateTimeOffset? MissingSince { get; set; }
    public string LabelState { get; set; } = VolumeLabelStates.Pending;
    public string ViolationsJson { get; set; } = "[]";
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    public IReadOnlySet<string> Violations() =>
        JsonColumn.Read<HashSet<string>>(ViolationsJson, new())!;

    public void SetViolations(IEnumerable<string> values) =>
        ViolationsJson = JsonColumn.Write(
            values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
}

public sealed class ClusterVolumeEntity
{
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Phase { get; set; } = "";
    public string? StorageClass { get; set; }
    public string? RequestedStorage { get; set; }
    public string? Capacity { get; set; }
    public DateTimeOffset CreatedAtK8s { get; set; }
    public DateTimeOffset? DeletionTimestamp { get; set; }
    public bool Mounted { get; set; }
    public string MountedByJson { get; set; } = "[]";
    public string LabelsJson { get; set; } = "{}";
    public string? LifecycleLabel { get; set; }
    public string? TenantLabel { get; set; }
    public string? OwnerLabel { get; set; }
    public string? ContractVersionLabel { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid LastScanId { get; set; }

    public IReadOnlyDictionary<string, string> Labels() =>
        JsonColumn.Read<Dictionary<string, string>>(LabelsJson, new())!;

    public IReadOnlyList<string> MountedBy() =>
        JsonColumn.Read<List<string>>(MountedByJson, [])!;
}

public sealed class ManagedNamespaceEntity
{
    public string Namespace { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string State { get; set; } = ManagedNamespaceStates.Active;
}

public sealed class ScanRunEntity
{
    public Guid Id { get; set; }
    public string Trigger { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int NamespacesScanned { get; set; }
    public int NamespacesSucceeded { get; set; }
    public int VolumeCount { get; set; }
    public string ErrorsJson { get; set; } = "[]";
}

public sealed class VolumeEventEntity
{
    public Guid Id { get; set; }
    public Guid? VolumeId { get; set; }
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string? TenantId { get; set; }
    public string Type { get; set; } = "";
    public string Actor { get; set; } = "";
    public string? Reason { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
