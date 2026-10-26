namespace OpenSandbox.Web.Infrastructure.Volumes;

public enum VolumeKind
{
    Unlabeled,
    Ephemeral,
    Business,
}

public static class VolumeClassifier
{
    public static VolumeKind Classify(string? lifecycleLabel)
    {
        if (string.IsNullOrWhiteSpace(lifecycleLabel))
            return VolumeKind.Unlabeled;

        var value = lifecycleLabel.Trim();
        return value switch
        {
            VolumeLifecycle.Ephemeral => VolumeKind.Ephemeral,
            VolumeLifecycle.Business => VolumeKind.Business,
            _ => VolumeKind.Business,
        };
    }

    public static string ToApiValue(VolumeKind kind) => kind switch
    {
        VolumeKind.Unlabeled => "unlabeled",
        VolumeKind.Ephemeral => VolumeLifecycle.Ephemeral,
        VolumeKind.Business => VolumeLifecycle.Business,
        _ => VolumeLifecycle.Business,
    };
}
