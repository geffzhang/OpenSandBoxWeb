using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeValuesTests
{
    [Theory]
    [InlineData(VolumeEventTypes.Registered, "registered")]
    [InlineData(VolumeEventTypes.Updated, "updated")]
    [InlineData(VolumeEventTypes.Reactivated, "reactivated")]
    [InlineData(VolumeEventTypes.RegistrationConflict, "registration-conflict")]
    [InlineData(VolumeEventTypes.LabelApplied, "label-applied")]
    [InlineData(VolumeEventTypes.LabelPatchFailed, "label-patch-failed")]
    [InlineData(VolumeEventTypes.LabelCorrected, "label-corrected")]
    [InlineData(VolumeEventTypes.LifecycleChanged, "lifecycle-changed")]
    [InlineData(VolumeEventTypes.Imported, "imported")]
    [InlineData(VolumeEventTypes.DriftDetected, "drift-detected")]
    [InlineData(VolumeEventTypes.DriftResolved, "drift-resolved")]
    [InlineData(VolumeEventTypes.Disappeared, "disappeared")]
    [InlineData(VolumeEventTypes.UnexpectedDisappearance, "unexpected-disappearance")]
    [InlineData(VolumeEventTypes.MissingBeforeObserved, "missing-before-observed")]
    [InlineData(VolumeEventTypes.NamespaceMassDisappearance, "namespace-mass-disappearance")]
    [InlineData(VolumeEventTypes.NamespaceUnmanaged, "namespace-unmanaged")]
    [InlineData(VolumeEventTypes.NamespaceManagedAgain, "namespace-managed-again")]
    [InlineData(VolumeEventTypes.ReappearedWithNewUid, "reappeared-with-new-uid")]
    [InlineData(VolumeEventTypes.TerminatingObserved, "terminating-observed")]
    public void EventTypes_AreStable(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData(VolumeStates.Active, "active")]
    [InlineData(VolumeStates.Missing, "missing")]
    [InlineData(VolumeStates.Unmanaged, "unmanaged")]
    [InlineData(VolumeStates.EphemeralGone, "ephemeral-gone")]
    [InlineData(VolumeStates.Unexpected, "unexpected")]
    [InlineData(VolumeStates.NamespaceDeleted, "namespace-deleted")]
    [InlineData(VolumeStates.NeverObserved, "never-observed")]
    public void StateAndReasonValues_AreStable(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData(VolumeLifecycle.Business, "business")]
    [InlineData(VolumeLifecycle.Ephemeral, "ephemeral")]
    public void LifecycleValues_AreStable(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData(VolumeLabelStates.Applied, "applied")]
    [InlineData(VolumeLabelStates.Pending, "pending")]
    public void LabelStateValues_AreStable(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData(ManagedNamespaceStates.Active, "active")]
    [InlineData(ManagedNamespaceStates.Gone, "gone")]
    [InlineData(ManagedNamespaceStates.Unmanaged, "unmanaged")]
    public void ManagedNamespaceStateValues_AreStable(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData("business", true)]
    [InlineData(" ephemeral ", true)]
    [InlineData("Business", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LifecycleIsValid_TrimsAndStaysCaseSensitive(string? value, bool expected)
        => Assert.Equal(expected, VolumeLifecycle.IsValid(value));
}
