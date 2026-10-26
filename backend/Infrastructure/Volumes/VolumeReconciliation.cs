namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record ObservedFacts(
    string Uid,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ReconcileInput(
    string TenantId,
    string Lifecycle,
    string State,
    string? StateReason,
    string? RegisteredUid,
    DateTimeOffset? LastSeenAt,
    string LabelState,
    IReadOnlySet<string> Violations,
    string? ExpectedOwnerLabel,
    ObservedFacts? Observed,
    bool NamespaceScanSucceeded);

public sealed record ReconcileResult(
    string State,
    string? StateReason,
    IReadOnlySet<string> Violations,
    IReadOnlyList<string> Events,
    bool ObservedThisScan,
    bool UidChanged);

public static class VolumeReconciliation
{
    private const string ReappearedMarker = "reappeared-with-new-uid";

    private static readonly string[] ContractLabels =
    [
        VolumeLabels.Lifecycle,
        VolumeLabels.Tenant,
        VolumeLabels.Owner,
        VolumeLabels.ContractVersion,
    ];

    public static ReconcileResult Reconcile(ReconcileInput input)
    {
        var events = new List<string>();

        if (!input.NamespaceScanSucceeded)
            return Unchanged(input, events);

        var observed = input.Observed;

        if (input.State == VolumeStates.Unmanaged)
        {
            if (observed is null)
                return Unchanged(input, events);

            events.Add(VolumeEventTypes.NamespaceManagedAgain);
            return new ReconcileResult(
                VolumeStates.Active,
                null,
                ActiveViolations(input, observed),
                events,
                true,
                HasUidChange(input, observed));
        }

        if (observed is null)
        {
            if (input.State != VolumeStates.Active)
                return Unchanged(input, events);

            if (input.LastSeenAt is null)
            {
                events.Add(VolumeEventTypes.MissingBeforeObserved);
                return new ReconcileResult(
                    VolumeStates.Missing, VolumeStates.NeverObserved,
                    new HashSet<string>(), events, false, false);
            }

            if (input.Lifecycle == VolumeLifecycle.Ephemeral)
            {
                events.Add(VolumeEventTypes.Disappeared);
                return new ReconcileResult(
                    VolumeStates.Missing, VolumeStates.EphemeralGone,
                    new HashSet<string>(), events, false, false);
            }

            events.Add(VolumeEventTypes.UnexpectedDisappearance);
            return new ReconcileResult(
                VolumeStates.Missing, VolumeStates.Unexpected,
                new HashSet<string>(), events, false, false);
        }

        var uidChanged = HasUidChange(input, observed);

        if (input.State == VolumeStates.Missing)
        {
            if (!uidChanged && input.RegisteredUid is not null)
            {
                events.Add(VolumeEventTypes.Reactivated);
                return new ReconcileResult(
                    VolumeStates.Active, null,
                    ActiveViolations(input, observed), events, true, false);
            }

            var violations = new HashSet<string>(input.Violations);
            if (violations.Add(ReappearedMarker))
                events.Add(VolumeEventTypes.ReappearedWithNewUid);

            return new ReconcileResult(
                VolumeStates.Missing, input.StateReason, violations,
                events, true, true);
        }

        if (input.State != VolumeStates.Active)
            return Unchanged(input, events);

        if (input.LabelState != VolumeLabelStates.Applied)
            return new ReconcileResult(
                VolumeStates.Active, null, new HashSet<string>(input.Violations),
                events, true, uidChanged);

        var next = ComputeViolations(input, observed);
        AddDriftEvents(input.Violations, next, events);
        return new ReconcileResult(
            VolumeStates.Active, null, next, events, true, uidChanged);
    }

    private static ReconcileResult Unchanged(ReconcileInput input, List<string> events) =>
        new(input.State, input.StateReason, new HashSet<string>(input.Violations),
            events, false, false);

    private static IReadOnlySet<string> ActiveViolations(
        ReconcileInput input, ObservedFacts observed) =>
        input.LabelState == VolumeLabelStates.Applied
            ? ComputeViolations(input, observed)
            : new HashSet<string>();

    private static bool HasUidChange(ReconcileInput input, ObservedFacts observed) =>
        input.RegisteredUid is not null &&
        !string.Equals(input.RegisteredUid, observed.Uid, StringComparison.Ordinal);

    private static HashSet<string> ComputeViolations(ReconcileInput input, ObservedFacts observed)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        var labels = observed.Labels;

        if (!ContractLabels.Any(labels.ContainsKey))
            violations.Add("labels-missing");

        if (labels.TryGetValue(VolumeLabels.Lifecycle, out var lifecycle))
        {
            var value = lifecycle.Trim();
            var known = value is VolumeLifecycle.Business or VolumeLifecycle.Ephemeral;
            if (!known)
                violations.Add($"unknown-lifecycle:{value}");
            if (!string.Equals(value, input.Lifecycle, StringComparison.Ordinal))
                violations.Add($"lifecycle-mismatch:{value}");
        }

        if (labels.TryGetValue(VolumeLabels.Tenant, out var tenant) &&
            !string.Equals(tenant, input.TenantId, StringComparison.Ordinal))
            violations.Add($"tenant-mismatch:{tenant}");

        if (input.ExpectedOwnerLabel is not null)
        {
            if (!labels.TryGetValue(VolumeLabels.Owner, out var owner))
                violations.Add("owner-mismatch:<missing>");
            else if (!string.Equals(owner, input.ExpectedOwnerLabel, StringComparison.Ordinal))
                violations.Add($"owner-mismatch:{owner}");
        }

        if (labels.TryGetValue(VolumeLabels.ContractVersion, out var version) &&
            !string.Equals(version, VolumeLabels.ContractVersionValue, StringComparison.Ordinal))
            violations.Add($"unsupported-contract-version:{version}");

        if (HasUidChange(input, observed))
            violations.Add("uid-changed");

        return violations;
    }

    private static void AddDriftEvents(
        IReadOnlySet<string> oldViolations,
        IReadOnlySet<string> newViolations,
        List<string> events)
    {
        var oldDrift = oldViolations.Where(value => value != ReappearedMarker)
            .ToHashSet(StringComparer.Ordinal);
        var newDrift = newViolations.Where(value => value != ReappearedMarker)
            .ToHashSet(StringComparer.Ordinal);

        if (newDrift.Except(oldDrift).Any())
            events.Add(VolumeEventTypes.DriftDetected);
        if (oldDrift.Except(newDrift).Any())
            events.Add(VolumeEventTypes.DriftResolved);
    }
}
