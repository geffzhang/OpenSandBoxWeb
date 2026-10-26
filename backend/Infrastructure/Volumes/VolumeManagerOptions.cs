namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class VolumeManagerOptions
{
    public const string SectionName = "VolumeManager";

    public bool Enabled { get; set; }
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool ScanOnStartup { get; set; } = true;
    public string ManagedNamespaceSelector { get; set; } = "opensandbox.io/volume-managed=true";
    public int MaxConcurrentNamespaces { get; set; } = 4;
    public string? KubeConfigPath { get; set; }
    public bool MigrateOnStartup { get; set; } = true;
}
