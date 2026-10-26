namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeLifecycle
{
    public const string Business = "business";
    public const string Ephemeral = "ephemeral";

    public static bool IsValid(string? value) => value?.Trim() is Business or Ephemeral;
}

public static class VolumeLabelStates
{
    public const string Applied = "applied";
    public const string Pending = "pending";
}

public static class VolumeStates
{
    public const string Active = "active";
    public const string Missing = "missing";
    public const string Unmanaged = "unmanaged";
    public const string EphemeralGone = "ephemeral-gone";
    public const string Unexpected = "unexpected";
    public const string NamespaceDeleted = "namespace-deleted";
    public const string NeverObserved = "never-observed";
}

public static class VolumeEventTypes
{
    public const string Registered = "registered";
    public const string Updated = "updated";
    public const string Reactivated = "reactivated";
    public const string RegistrationConflict = "registration-conflict";
    public const string LabelApplied = "label-applied";
    public const string LabelPatchFailed = "label-patch-failed";
    public const string LabelCorrected = "label-corrected";
    public const string LifecycleChanged = "lifecycle-changed";
    public const string Imported = "imported";
    public const string DriftDetected = "drift-detected";
    public const string DriftResolved = "drift-resolved";
    public const string Disappeared = "disappeared";
    public const string UnexpectedDisappearance = "unexpected-disappearance";
    public const string MissingBeforeObserved = "missing-before-observed";
    public const string NamespaceMassDisappearance = "namespace-mass-disappearance";
    public const string NamespaceUnmanaged = "namespace-unmanaged";
    public const string NamespaceManagedAgain = "namespace-managed-again";
    public const string ReappearedWithNewUid = "reappeared-with-new-uid";
    public const string TerminatingObserved = "terminating-observed";
}

public static class ManagedNamespaceStates
{
    public const string Active = "active";
    public const string Gone = "gone";
    public const string Unmanaged = "unmanaged";
}
