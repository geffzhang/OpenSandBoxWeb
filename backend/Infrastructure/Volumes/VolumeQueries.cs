using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class VolumeListQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Namespace { get; set; }
    public string? Tenant { get; set; }
    public string? Classification { get; set; }
    public string? Lifecycle { get; set; }
    public bool? Mounted { get; set; }
    public string? Q { get; set; }
    public string Sort { get; set; } = "namespace";
    public string Order { get; set; } = "asc";
}

public sealed class VolumeQueries(VolumeDbContext db)
{
    private const int MaxPageSize = 200;
    private const int MaxViolations = 200;

    public async Task<VolumeListResponse> ListAsync(
        VolumeListQuery query,
        string? forcedTenant,
        CancellationToken ct)
    {
        var rows = await LoadRowsAsync(ct);
        IEnumerable<VolumeListItemDto> filtered = rows;

        if (!string.IsNullOrWhiteSpace(query.Namespace))
        {
            var namespaceName = query.Namespace.Trim();
            filtered = filtered.Where(item => item.Namespace == namespaceName);
        }

        if (forcedTenant is not null)
        {
            // 租户能看到台账行，以及自己名下命名空间中的观测行；其余观测行不可见。
            var ownedNamespaces = (await db.TenantNamespaces.AsNoTracking()
                    .Where(mapping => mapping.TenantId == forcedTenant)
                    .Select(mapping => mapping.Namespace)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            filtered = filtered.Where(item =>
                item.Tenant == forcedTenant &&
                (item.Id is not null || ownedNamespaces.Contains(item.Namespace)));
        }
        else if (!string.IsNullOrWhiteSpace(query.Tenant))
        {
            var tenantId = query.Tenant.Trim();
            filtered = filtered.Where(item => item.Tenant == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Classification))
        {
            var classification = query.Classification.Trim();
            filtered = filtered.Where(item => item.Classification == classification);
        }

        if (!string.IsNullOrWhiteSpace(query.Lifecycle))
        {
            var lifecycle = query.Lifecycle.Trim();
            filtered = filtered.Where(item => item.Lifecycle == lifecycle);
        }

        if (query.Mounted is not null)
            filtered = filtered.Where(item => item.Mounted == query.Mounted);

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim();
            filtered = filtered.Where(item =>
                Contains(item.ClaimName, term) ||
                Contains(item.OwnerKind, term) ||
                Contains(item.OwnerId, term) ||
                Contains(item.Tenant, term) ||
                Contains(item.TenantName, term));
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var list = filtered.ToList();
        var totalItems = list.Count;
        var totalPages = totalItems == 0
            ? 0
            : (int)Math.Ceiling(totalItems / (double)pageSize);
        var descending = string.Equals(query.Order, "desc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<VolumeListItemDto> ordered =
            (query.Sort ?? "namespace").ToLowerInvariant() switch
            {
                "claimname" => descending
                    ? list.OrderByDescending(item => item.ClaimName, StringComparer.Ordinal)
                    : list.OrderBy(item => item.ClaimName, StringComparer.Ordinal),
                "createdat" => descending
                    ? list.OrderByDescending(item => item.CreatedAt)
                    : list.OrderBy(item => item.CreatedAt),
                "lastseenat" => descending
                    ? list.OrderByDescending(item => item.LastSeenAt)
                    : list.OrderBy(item => item.LastSeenAt),
                "tenant" => descending
                    ? list.OrderByDescending(item => item.Tenant ?? "", StringComparer.Ordinal)
                    : list.OrderBy(item => item.Tenant ?? "", StringComparer.Ordinal),
                _ => descending
                    ? list.OrderByDescending(item => item.Namespace, StringComparer.Ordinal)
                    : list.OrderBy(item => item.Namespace, StringComparer.Ordinal),
            };

        var stable = ordered
            .ThenBy(item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(item => item.ClaimName, StringComparer.Ordinal);
        // 用 long 计算偏移，避免极大页码下 int 溢出让 Skip 回绕到第一页。
        var skip = (long)(page - 1) * pageSize;
        VolumeListItemDto[] items = skip >= totalItems
            ? []
            : stable.Skip((int)skip).Take(pageSize).ToArray();

        return new VolumeListResponse(items, new PaginationInfoDto(
            page, pageSize, totalItems, totalPages, page < totalPages));
    }

    public async Task<VolumeDetailDto?> GetAsync(
        string namespaceName,
        string claimName,
        string? forcedTenant,
        CancellationToken ct)
    {
        var ledger = await db.Volumes.AsNoTracking().SingleOrDefaultAsync(
            volume => volume.Namespace == namespaceName && volume.ClaimName == claimName, ct);
        var observed = await db.ClusterVolumes.AsNoTracking().SingleOrDefaultAsync(
            volume => volume.Namespace == namespaceName && volume.ClaimName == claimName, ct);

        if (ledger is null && observed is null)
            return null;
        if (forcedTenant is not null &&
            (ledger is null || ledger.TenantId != forcedTenant))
            return null;

        var tenantName = ledger is null
            ? null
            : await db.Tenants.AsNoTracking()
                .Where(tenant => tenant.Id == ledger.TenantId)
                .Select(tenant => tenant.DisplayName)
                .SingleOrDefaultAsync(ct);

        // SQLite 无法翻译 DateTimeOffset 排序，先物化再按创建时间倒序取 100 条。
        var events = (await db.VolumeEvents.AsNoTracking()
                .Where(entry => entry.Namespace == namespaceName && entry.ClaimName == claimName)
                .ToListAsync(ct))
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(100)
            .ToArray();

        return new VolumeDetailDto(
            ToListItem(ledger, observed, tenantName),
            observed is null ? null : ToObservedDto(observed),
            events.Select(ToEventDto).ToArray());
    }

    public async Task<VolumeReportDto> ReportAsync(CancellationToken ct)
    {
        var rows = await LoadRowsAsync(ct);
        var observations = await db.ClusterVolumes.AsNoTracking().ToListAsync(ct);
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(ct);
        var observed = observations.Count;
        var labeled = observations.Count(volume =>
            !string.IsNullOrWhiteSpace(volume.LifecycleLabel));
        var registeredKeys = rows
            .Where(item => item.Id is not null)
            .Select(item => (item.Namespace, item.ClaimName))
            .ToHashSet();
        var registered = observations.Count(volume =>
            registeredKeys.Contains((volume.Namespace, volume.ClaimName)));

        var counts = new VolumeReportCounts(
            observed,
            rows.Count(item => item.Classification == VolumeClassifications.Registered),
            rows.Count(item => item.Classification == VolumeClassifications.Unlabeled),
            rows.Count(item => item.Classification == VolumeClassifications.Unregistered),
            rows.Count(item => item.Classification == VolumeClassifications.Drift),
            rows.Count(item => item.Classification == VolumeClassifications.Missing),
            rows.Count(item => item.Classification == VolumeClassifications.Reappeared),
            rows.Count(item => item.Classification == VolumeClassifications.Unmanaged));

        var coverage = new VolumeCoverageDto(
            observed, labeled, registered,
            Percent(labeled, observed), Percent(registered, observed));

        var byTenant = rows
            .GroupBy(item => item.Tenant ?? "未登记租户")
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new VolumeDistributionItem(group.Key, group.Count()))
            .ToArray();

        var byNamespace = rows
            .GroupBy(item => item.Namespace)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new VolumeDistributionItem(group.Key, group.Count()))
            .ToArray();

        var violations = new List<VolumeViolationDto>();
        foreach (var item in rows)
        {
            switch (item.Classification)
            {
                case VolumeClassifications.Unlabeled:
                    violations.Add(new VolumeViolationDto(
                        "unlabeled", item.Namespace, item.ClaimName, "缺少 lifecycle 标签"));
                    break;
                case VolumeClassifications.Unregistered:
                    violations.Add(new VolumeViolationDto(
                        "unregistered", item.Namespace, item.ClaimName, "有 lifecycle 标签但未登记"));
                    break;
                case VolumeClassifications.Drift:
                    violations.Add(new VolumeViolationDto(
                        "drift", item.Namespace, item.ClaimName,
                        $"漂移: {string.Join(", ", item.Violations)}"));
                    break;
                case VolumeClassifications.Missing when item.StateReason == VolumeStates.Unexpected:
                    violations.Add(new VolumeViolationDto(
                        "unexpected-disappearance", item.Namespace, item.ClaimName,
                        "business 卷意外消失"));
                    break;
                case VolumeClassifications.Missing when item.StateReason == VolumeStates.NeverObserved:
                    violations.Add(new VolumeViolationDto(
                        "missing-before-observed", item.Namespace, item.ClaimName,
                        "注册后从未观测即消失"));
                    break;
                case VolumeClassifications.Unmanaged:
                    violations.Add(new VolumeViolationDto(
                        "unmanaged", item.Namespace, item.ClaimName, "命名空间不再受管"));
                    break;
                case VolumeClassifications.Reappeared:
                    violations.Add(new VolumeViolationDto(
                        "reappeared", item.Namespace, item.ClaimName,
                        "同名 PVC 以新 UID 重新出现"));
                    break;
            }
        }

        var knownTenants = tenants.Select(tenant => tenant.Id)
            .ToHashSet(StringComparer.Ordinal);
        // 仅观测行参与未知租户判定；台账行由漂移检查覆盖。
        foreach (var volume in observations.Where(volume =>
                     !registeredKeys.Contains((volume.Namespace, volume.ClaimName)) &&
                     !string.IsNullOrWhiteSpace(volume.TenantLabel) &&
                     !knownTenants.Contains(volume.TenantLabel)))
        {
            violations.Add(new VolumeViolationDto(
                "unknown-tenant", volume.Namespace, volume.ClaimName,
                $"未知租户标签: {volume.TenantLabel}"));
        }

        // 排序后再截断，保证分页结果确定（同类型按命名空间、卷名升序）。
        var orderedViolations = violations
            .OrderBy(violation => violation.Type, StringComparer.Ordinal)
            .ThenBy(violation => violation.Namespace, StringComparer.Ordinal)
            .ThenBy(violation => violation.ClaimName, StringComparer.Ordinal)
            .ToList();
        var truncated = orderedViolations.Count > MaxViolations;
        var violationPage = orderedViolations.Take(MaxViolations).ToArray();

        // SQLite 无法翻译 DateTimeOffset 排序，先物化再按开始时间倒序取最近一次扫描。
        var lastScan = (await db.ScanRuns.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefault();

        return new VolumeReportDto(
            lastScan is null ? null : ToScanRunDto(lastScan),
            counts,
            coverage,
            byTenant,
            byNamespace,
            violationPage,
            truncated);
    }

    private async Task<List<VolumeListItemDto>> LoadRowsAsync(CancellationToken ct)
    {
        var ledger = await db.Volumes.AsNoTracking().ToListAsync(ct);
        var observations = (await db.ClusterVolumes.AsNoTracking().ToListAsync(ct))
            .ToDictionary(volume => (volume.Namespace, volume.ClaimName));
        var tenants = (await db.Tenants.AsNoTracking().ToListAsync(ct))
            .ToDictionary(tenant => tenant.Id, tenant => tenant.DisplayName);
        var ledgerKeys = ledger
            .Select(volume => (volume.Namespace, volume.ClaimName))
            .ToHashSet();

        var rows = new List<VolumeListItemDto>(ledger.Count + observations.Count);
        foreach (var volume in ledger)
        {
            rows.Add(ToListItem(
                volume,
                observations.GetValueOrDefault((volume.Namespace, volume.ClaimName)),
                tenants.GetValueOrDefault(volume.TenantId)));
        }

        foreach (var observation in observations.Values.Where(volume =>
                     !ledgerKeys.Contains((volume.Namespace, volume.ClaimName))))
        {
            rows.Add(ToListItem(null, observation, null));
        }

        return rows;
    }

    private static VolumeListItemDto ToListItem(
        VolumeEntity? ledger,
        ClusterVolumeEntity? observed,
        string? tenantName)
    {
        if (ledger is null && observed is null)
            throw new ArgumentException("台账和观测至少有一个");

        var lifecycle = ledger?.Lifecycle;
        if (lifecycle is null && observed is not null)
        {
            lifecycle = VolumeClassifier.ToApiValue(
                VolumeClassifier.Classify(observed.LifecycleLabel));
        }

        return new VolumeListItemDto(
            ledger?.Id,
            ledger?.Namespace ?? observed!.Namespace,
            ledger?.ClaimName ?? observed!.ClaimName,
            ledger?.TenantId ?? observed?.TenantLabel,
            tenantName,
            ledger?.OwnerKind,
            ledger?.OwnerId,
            ledger?.Role,
            ledger?.MountPath,
            lifecycle,
            ledger?.State,
            ledger?.StateReason,
            Classify(ledger, observed),
            ledger?.LabelState,
            ledger?.Violations().OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [],
            ledger?.RegisteredUid,
            observed?.Uid,
            ledger?.RegisteredAt,
            ledger?.LastSeenAt ?? observed?.LastSeenAt,
            observed?.Mounted,
            observed?.StorageClass,
            observed?.Capacity,
            observed?.CreatedAtK8s ?? ledger?.RegisteredAt);
    }

    private static string Classify(VolumeEntity? ledger, ClusterVolumeEntity? observed)
    {
        if (ledger is null)
        {
            return string.IsNullOrWhiteSpace(observed?.LifecycleLabel)
                ? VolumeClassifications.Unlabeled
                : VolumeClassifications.Unregistered;
        }

        if (ledger.State == VolumeStates.Unmanaged)
            return VolumeClassifications.Unmanaged;

        if (ledger.State == VolumeStates.Missing)
        {
            return observed is not null &&
                   (ledger.RegisteredUid is null || ledger.RegisteredUid != observed.Uid)
                ? VolumeClassifications.Reappeared
                : VolumeClassifications.Missing;
        }

        if (observed is not null && ledger.Violations().Count > 0)
            return VolumeClassifications.Drift;

        return VolumeClassifications.Registered;
    }

    private static ClusterVolumeDto ToObservedDto(ClusterVolumeEntity volume) => new(
        volume.Namespace,
        volume.ClaimName,
        volume.Uid,
        volume.Phase,
        volume.StorageClass,
        volume.RequestedStorage,
        volume.Capacity,
        volume.CreatedAtK8s,
        volume.DeletionTimestamp,
        volume.Mounted,
        volume.MountedBy(),
        volume.Labels(),
        volume.LifecycleLabel,
        volume.TenantLabel,
        volume.OwnerLabel,
        volume.ContractVersionLabel,
        volume.FirstSeenAt,
        volume.LastSeenAt);

    private static VolumeEventDto ToEventDto(VolumeEventEntity entry) => new(
        entry.Id,
        entry.VolumeId,
        entry.Namespace,
        entry.ClaimName,
        entry.TenantId,
        entry.Type,
        entry.Actor,
        entry.Reason,
        entry.DataJson,
        entry.CreatedAt);

    private static ScanRunDto ToScanRunDto(ScanRunEntity run) => new(
        run.Id,
        run.Trigger,
        run.StartedAt,
        run.FinishedAt,
        run.NamespacesScanned,
        run.NamespacesSucceeded,
        run.VolumeCount,
        JsonColumn.Read<List<ScanErrorDto>>(run.ErrorsJson, []));

    private static bool Contains(string? value, string term) =>
        value is not null &&
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static decimal Percent(int part, int total) =>
        total == 0 ? 0m : Math.Round(part * 100m / total, 1);
}
