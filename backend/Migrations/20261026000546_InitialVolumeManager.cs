using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSandbox.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialVolumeManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cluster_volumes",
                columns: table => new
                {
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ClaimName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageClass = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    RequestedStorage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Capacity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtK8s = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletionTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Mounted = table.Column<bool>(type: "boolean", nullable: false),
                    MountedByJson = table.Column<string>(type: "jsonb", nullable: false),
                    LabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LifecycleLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantLabel = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    OwnerLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContractVersionLabel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastScanId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_volumes", x => new { x.Namespace, x.ClaimName });
                });

            migrationBuilder.CreateTable(
                name: "managed_namespaces",
                columns: table => new
                {
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_namespaces", x => x.Namespace);
                });

            migrationBuilder.CreateTable(
                name: "scan_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NamespacesScanned = table.Column<int>(type: "integer", nullable: false),
                    NamespacesSucceeded = table.Column<int>(type: "integer", nullable: false),
                    VolumeCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "volume_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VolumeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ClaimName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(63)", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_api_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_api_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_namespaces",
                columns: table => new
                {
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(63)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_namespaces", x => x.Namespace);
                    table.ForeignKey(
                        name: "FK_tenant_namespaces_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "volumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ClaimName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MountPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastSandboxId = table.Column<string>(type: "text", nullable: true),
                    RegisteredUid = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StateReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MissingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LabelState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ViolationsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volumes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_volumes_LastScanId",
                table: "cluster_volumes",
                column: "LastScanId");

            migrationBuilder.CreateIndex(
                name: "IX_cluster_volumes_Uid",
                table: "cluster_volumes",
                column: "Uid");

            migrationBuilder.CreateIndex(
                name: "IX_scan_runs_StartedAt",
                table: "scan_runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_KeyHash",
                table: "tenant_api_keys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_TenantId",
                table: "tenant_api_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_namespaces_TenantId",
                table: "tenant_namespaces",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_volume_events_Namespace_ClaimName_CreatedAt",
                table: "volume_events",
                columns: new[] { "Namespace", "ClaimName", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_volume_events_VolumeId",
                table: "volume_events",
                column: "VolumeId");

            migrationBuilder.CreateIndex(
                name: "IX_volumes_Lifecycle",
                table: "volumes",
                column: "Lifecycle");

            migrationBuilder.CreateIndex(
                name: "IX_volumes_Namespace_ClaimName",
                table: "volumes",
                columns: new[] { "Namespace", "ClaimName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volumes_State",
                table: "volumes",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_volumes_TenantId",
                table: "volumes",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_volumes");

            migrationBuilder.DropTable(
                name: "managed_namespaces");

            migrationBuilder.DropTable(
                name: "scan_runs");

            migrationBuilder.DropTable(
                name: "tenant_api_keys");

            migrationBuilder.DropTable(
                name: "tenant_namespaces");

            migrationBuilder.DropTable(
                name: "volume_events");

            migrationBuilder.DropTable(
                name: "volumes");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
