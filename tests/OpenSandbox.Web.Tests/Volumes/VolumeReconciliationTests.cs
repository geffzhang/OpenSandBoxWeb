using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeReconciliationTests
{
    private static ReconcileInput Input(
        string state = VolumeStates.Active,
        string lifecycle = VolumeLifecycle.Business,
        string? registeredUid = "uid-1",
        DateTimeOffset? lastSeenAt = null,
        string labelState = VolumeLabelStates.Applied,
        IReadOnlySet<string>? violations = null,
        ObservedFacts? observed = null,
        bool scanSucceeded = true,
        string? expectedOwner = "employee-1")
        => new(
            TenantId: "goodcrew",
            Lifecycle: lifecycle,
            State: state,
            StateReason: null,
            RegisteredUid: registeredUid,
            LastSeenAt: lastSeenAt,
            LabelState: labelState,
            Violations: violations ?? new HashSet<string>(),
            ExpectedOwnerLabel: expectedOwner,
            Observed: observed,
            NamespaceScanSucceeded: scanSucceeded);

    private static ObservedFacts Observed(
        string uid = "uid-1",
        string? lifecycle = "business",
        string? tenant = "goodcrew",
        string? owner = "employee-1",
        string? version = "1")
    {
        var labels = new Dictionary<string, string>();
        if (lifecycle is not null) labels[VolumeLabels.Lifecycle] = lifecycle;
        if (tenant is not null) labels[VolumeLabels.Tenant] = tenant;
        if (owner is not null) labels[VolumeLabels.Owner] = owner;
        if (version is not null) labels[VolumeLabels.ContractVersion] = version;
        return new ObservedFacts(uid, labels);
    }

    [Fact]
    public void ActiveObserved_StaysActive()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            lastSeenAt: DateTimeOffset.UnixEpoch,
            observed: Observed()));

        Assert.Equal(VolumeStates.Active, result.State);
        Assert.True(result.ObservedThisScan);
        Assert.Empty(result.Events);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ScanFailure_DoesNotTransition()
    {
        var result = VolumeReconciliation.Reconcile(Input(scanSucceeded: false));

        Assert.Equal(VolumeStates.Active, result.State);
        Assert.Empty(result.Events);
        Assert.False(result.ObservedThisScan);
    }

    [Fact]
    public void NeverObservedThenMissing_HighPriorityEvent()
    {
        var result = VolumeReconciliation.Reconcile(Input(lastSeenAt: null));

        Assert.Equal(VolumeStates.Missing, result.State);
        Assert.Equal(VolumeStates.NeverObserved, result.StateReason);
        Assert.Contains(VolumeEventTypes.MissingBeforeObserved, result.Events);
        Assert.DoesNotContain(VolumeEventTypes.UnexpectedDisappearance, result.Events);
    }

    [Fact]
    public void EphemeralThenMissing_IsNormalDisappearance()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            lifecycle: VolumeLifecycle.Ephemeral,
            lastSeenAt: DateTimeOffset.UnixEpoch));

        Assert.Equal(VolumeStates.Missing, result.State);
        Assert.Equal(VolumeStates.EphemeralGone, result.StateReason);
        Assert.Contains(VolumeEventTypes.Disappeared, result.Events);
        Assert.DoesNotContain(VolumeEventTypes.UnexpectedDisappearance, result.Events);
    }

    [Fact]
    public void BusinessThenMissing_IsUnexpected()
    {
        var result = VolumeReconciliation.Reconcile(Input(lastSeenAt: DateTimeOffset.UnixEpoch));

        Assert.Equal(VolumeStates.Missing, result.State);
        Assert.Equal(VolumeStates.Unexpected, result.StateReason);
        Assert.Contains(VolumeEventTypes.UnexpectedDisappearance, result.Events);
    }

    [Fact]
    public void MissingWithSameUid_Reactivates()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            state: VolumeStates.Missing,
            observed: Observed()));

        Assert.Equal(VolumeStates.Active, result.State);
        Assert.Null(result.StateReason);
        Assert.Contains(VolumeEventTypes.Reactivated, result.Events);
    }

    [Fact]
    public void MissingWithNewUid_StaysMissingAndEmitsOnce()
    {
        var first = VolumeReconciliation.Reconcile(Input(
            state: VolumeStates.Missing,
            observed: Observed(uid: "uid-2")));

        Assert.Equal(VolumeStates.Missing, first.State);
        Assert.Contains(VolumeEventTypes.ReappearedWithNewUid, first.Events);
        Assert.Contains("reappeared-with-new-uid", first.Violations);

        var second = VolumeReconciliation.Reconcile(Input(
            state: VolumeStates.Missing,
            violations: first.Violations,
            observed: Observed(uid: "uid-2")));

        Assert.Equal(VolumeStates.Missing, second.State);
        Assert.DoesNotContain(VolumeEventTypes.ReappearedWithNewUid, second.Events);
    }

    [Fact]
    public void UnmanagedNamespaceWithObservation_Reactivates()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            state: VolumeStates.Unmanaged,
            observed: Observed()));

        Assert.Equal(VolumeStates.Active, result.State);
        Assert.Contains(VolumeEventTypes.NamespaceManagedAgain, result.Events);
    }

    [Fact]
    public void PendingLabel_DoesNotCompareLabels()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            labelState: VolumeLabelStates.Pending,
            observed: Observed(lifecycle: "ephemeral", tenant: "other", owner: "x")));

        Assert.Empty(result.Violations);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void LabelsMissing_IsHighPriorityDrift()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            observed: new ObservedFacts("uid-1", new Dictionary<string, string>())));

        Assert.Contains("labels-missing", result.Violations);
        Assert.Contains(VolumeEventTypes.DriftDetected, result.Events);
    }

    [Fact]
    public void LifecycleMismatch_AndUnknownLifecycle()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            observed: Observed(lifecycle: "archived")));

        Assert.Contains("unknown-lifecycle:archived", result.Violations);
        Assert.Contains("lifecycle-mismatch:archived", result.Violations);
    }

    [Fact]
    public void TenantOwnerAndVersionMismatches()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            observed: Observed(tenant: "someone", owner: "employee-2", version: "2")));

        Assert.Contains("tenant-mismatch:someone", result.Violations);
        Assert.Contains("owner-mismatch:employee-2", result.Violations);
        Assert.Contains("unsupported-contract-version:2", result.Violations);
    }

    [Fact]
    public void ExpectedOwnerMissing_IsDrift()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            observed: Observed(owner: null)));

        Assert.Contains("owner-mismatch:<missing>", result.Violations);
    }

    [Fact]
    public void UidChanged_OnActiveRow()
    {
        var result = VolumeReconciliation.Reconcile(Input(observed: Observed(uid: "uid-2")));

        Assert.Equal(VolumeStates.Active, result.State);
        Assert.Contains("uid-changed", result.Violations);
    }

    [Fact]
    public void ResolvedDrift_EmitsDriftResolved()
    {
        var result = VolumeReconciliation.Reconcile(Input(
            violations: new HashSet<string> { "tenant-mismatch:someone" },
            observed: Observed()));

        Assert.Empty(result.Violations);
        Assert.Contains(VolumeEventTypes.DriftResolved, result.Events);
    }
}
