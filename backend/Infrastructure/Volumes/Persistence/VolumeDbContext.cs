using Microsoft.EntityFrameworkCore;

namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public sealed class VolumeDbContext(DbContextOptions<VolumeDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<TenantNamespaceEntity> TenantNamespaces => Set<TenantNamespaceEntity>();
    public DbSet<TenantApiKeyEntity> TenantApiKeys => Set<TenantApiKeyEntity>();
    public DbSet<VolumeEntity> Volumes => Set<VolumeEntity>();
    public DbSet<ClusterVolumeEntity> ClusterVolumes => Set<ClusterVolumeEntity>();
    public DbSet<ManagedNamespaceEntity> ManagedNamespaces => Set<ManagedNamespaceEntity>();
    public DbSet<ScanRunEntity> ScanRuns => Set<ScanRunEntity>();
    public DbSet<VolumeEventEntity> VolumeEvents => Set<VolumeEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jsonType = Database.IsNpgsql() ? "jsonb" : "TEXT";
        var hashType = Database.IsNpgsql() ? "bytea" : "BLOB";

        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(63);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(16);
        });

        modelBuilder.Entity<TenantNamespaceEntity>(entity =>
        {
            entity.ToTable("tenant_namespaces");
            entity.HasKey(x => x.Namespace);
            entity.Property(x => x.Namespace).HasMaxLength(253);
            entity.HasIndex(x => x.TenantId);
            entity.HasOne(x => x.Tenant).WithMany(x => x.Namespaces)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantApiKeyEntity>(entity =>
        {
            entity.ToTable("tenant_api_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.KeyHash).HasColumnType(hashType);
            entity.Property(x => x.Prefix).HasMaxLength(8);
            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasOne(x => x.Tenant).WithMany(x => x.ApiKeys)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VolumeEntity>(entity =>
        {
            entity.ToTable("volumes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Namespace).HasMaxLength(253);
            entity.Property(x => x.ClaimName).HasMaxLength(253);
            entity.Property(x => x.TenantId).HasMaxLength(63);
            entity.Property(x => x.OwnerKind).HasMaxLength(32);
            entity.Property(x => x.OwnerId).HasMaxLength(128);
            entity.Property(x => x.Role).HasMaxLength(32);
            entity.Property(x => x.MountPath).HasMaxLength(128);
            entity.Property(x => x.Lifecycle).HasMaxLength(16);
            entity.Property(x => x.ContractVersion).HasMaxLength(16);
            entity.Property(x => x.State).HasMaxLength(16);
            entity.Property(x => x.StateReason).HasMaxLength(32);
            entity.Property(x => x.LabelState).HasMaxLength(16);
            entity.Property(x => x.ViolationsJson).HasColumnName("Violations").HasColumnType(jsonType);
            entity.HasIndex(x => new { x.Namespace, x.ClaimName }).IsUnique();
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => x.Lifecycle);
            entity.HasOne<TenantEntity>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClusterVolumeEntity>(entity =>
        {
            entity.ToTable("cluster_volumes");
            entity.HasKey(x => new { x.Namespace, x.ClaimName });
            entity.Property(x => x.Namespace).HasMaxLength(253);
            entity.Property(x => x.ClaimName).HasMaxLength(253);
            entity.Property(x => x.Uid).HasMaxLength(128);
            entity.Property(x => x.Phase).HasMaxLength(32);
            entity.Property(x => x.StorageClass).HasMaxLength(253);
            entity.Property(x => x.RequestedStorage).HasMaxLength(64);
            entity.Property(x => x.Capacity).HasMaxLength(64);
            entity.Property(x => x.MountedByJson).HasColumnName("MountedBy").HasColumnType(jsonType);
            entity.Property(x => x.LabelsJson).HasColumnName("Labels").HasColumnType(jsonType);
            entity.Property(x => x.LifecycleLabel).HasMaxLength(64);
            entity.Property(x => x.TenantLabel).HasMaxLength(253);
            entity.Property(x => x.OwnerLabel).HasMaxLength(64);
            entity.Property(x => x.ContractVersionLabel).HasMaxLength(16);
            entity.HasIndex(x => x.Uid);
            entity.HasIndex(x => x.LastScanId);
            entity.HasOne<ScanRunEntity>().WithMany()
                .HasForeignKey(x => x.LastScanId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManagedNamespaceEntity>(entity =>
        {
            entity.ToTable("managed_namespaces");
            entity.HasKey(x => x.Namespace);
            entity.Property(x => x.Namespace).HasMaxLength(253);
            entity.Property(x => x.State).HasMaxLength(16);
        });

        modelBuilder.Entity<ScanRunEntity>(entity =>
        {
            entity.ToTable("scan_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Trigger).HasMaxLength(16);
            entity.Property(x => x.ErrorsJson).HasColumnName("Errors").HasColumnType(jsonType);
            entity.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<VolumeEventEntity>(entity =>
        {
            entity.ToTable("volume_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Namespace).HasMaxLength(253);
            entity.Property(x => x.ClaimName).HasMaxLength(253);
            entity.Property(x => x.TenantId).HasMaxLength(63);
            entity.Property(x => x.Type).HasMaxLength(64);
            entity.Property(x => x.DataJson).HasColumnName("Data").HasColumnType(jsonType);
            entity.HasIndex(x => new { x.Namespace, x.ClaimName, x.CreatedAt });
            entity.HasIndex(x => x.VolumeId);
        });
    }
}
