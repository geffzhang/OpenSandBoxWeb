# Volume Manager API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在核心域之上提供可用的 HTTP API：租户 Key 认证、卷注册与生命周期、台账查询/详情/报表、租户与 Key 管理、导入预检、手动扫描。

**Architecture:** 租户流量走独立 `/api/v1/tenant/*` 前缀和独立 Key 中间件，控制台流量走 Keycloak JWT（由认证计划提供）。业务逻辑放在 scoped 服务中，端点只做参数绑定与结果映射；领域异常由独立中间件转换为 400/401/404/409/503。

**Tech Stack:** .NET 10 Minimal APIs、EF Core 10、xUnit 2.9.3、SQLite（测试）、`WebApplicationFactory`。

**Spec:** `docs/superpowers/specs/2026-10-25-global-volume-manager-design.md`（§4.1 FR-1/FR-3/FR-4/FR-8/FR-9/FR-10/FR-11/FR-13、§6.4–§6.9）

**Prerequisite:** `docs/superpowers/plans/2026-10-25-volume-manager-core.md` 与 `docs/superpowers/plans/2026-10-25-console-keycloak-auth.md` 已执行完毕。

## Global Constraints

- `/api/v1/tenant/*` 只接受租户 API Key；`/api/v1/volumes*`、`/api/v1/volume-tenants*` 只接受 Keycloak JWT。两类凭据不得互用。
- 租户 Key：32 字节随机、base64url 43 字符、SHA-256 存储、前缀 8 字符、明文只返回一次、可吊销、`lastUsedAt` 节流到 5 分钟。
- 注册响应始终 200 + 逐项结果；仅当 K8s/DB 不可用时整体 503。
- 注册冲突矩阵：不同租户/不同 owner → `conflict`；同租户同 owner 但 lifecycle 不同 → `conflict`；同租户同 owner 且 PVC 存在 → `updated` 或 `reactivated`。
- 同一租户内 owner 已绑定其他 `(namespace, claimName)` → `conflict`（跨命名空间同样判定），禁止静默改绑。
- 生命周期变更只更新 lifecycle 标签；降级到 `ephemeral` 必须带 `reason`（≤512）。
- 没有删除卷的端点。
- 查询语义：筛选 → 搜索 → 排序 → 分页；`totalItems` 是筛选后总数；同值以 namespace+claimName 升序兜底。
- 报表与筛选基于合并视图（台账 + 观测），观测表中未被登记的卷也可见。
- 导入默认 dry-run；dry-run 不写库、不打标签。
- 管理端 DB 不可用返回 503；不得把 DB 异常伪装成 404。
- 所有 UI/日志文案中文；字段 JSON 使用全局 camelCase。
- 本计划不实现控制台页面；页面在 `2026-10-25-volume-manager-console-ops.md`。

---

### Task 1: API 契约、字段校验、Key 原语

**Files:**

- Create: `backend/Contracts/VolumeContracts.cs`
- Create: `backend/Infrastructure/Volumes/VolumeValidation.cs`
- Create: `backend/Infrastructure/Volumes/ApiKeys.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeValidationTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/ApiKeysTests.cs`

**Interfaces:**

- Produces: 全部请求/响应 DTO（见下方代码）
- Produces: `VolumeValidation.TenantId|Namespace|ClaimName|Text|ValidateRegistration|ValidateImportItem`
- Produces: `ApiKeys.Create(): string`、`ApiKeys.Hash(string): byte[]`、`ApiKeys.Prefix(string): string`
- Produces: `RegistrationStatuses`、`VolumeClassifications` 常量

- [ ] **Step 1: 写校验与 Key 失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeValidationTests.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeValidationTests
{
    [Theory]
    [InlineData("goodcrew", "goodcrew")]
    [InlineData(" goodcrew ", "goodcrew")]
    [InlineData("team-1", "team-1")]
    [InlineData("Team", null)]
    [InlineData("team_1", null)]
    [InlineData("-team", null)]
    [InlineData("team-", null)]
    [InlineData("", null)]
    public void TenantId_MatchesSlug(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.TenantId(input));

    [Theory]
    [InlineData("opensandbox", "opensandbox")]
    [InlineData("team-a", "team-a")]
    [InlineData("Team", null)]
    public void Namespace_MatchesDnsLabel(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.Namespace(input));

    [Fact]
    public void Namespace_RejectsOverlongLabel()
        => Assert.Null(VolumeValidation.Namespace(new string('a', 64)));

    [Theory]
    [InlineData("pvc-1", "pvc-1")]
    [InlineData("app.data", "app.data")]
    [InlineData("app..data", null)]
    [InlineData("-pvc", null)]
    public void ClaimName_MatchesDnsSubdomain(string? input, string? expected)
        => Assert.Equal(expected, VolumeValidation.ClaimName(input));

    private static VolumeRegistrationItem Item(
        string claimName = "pvc-1",
        string ownerKind = "employee",
        string ownerId = "3f2a",
        string? role = "meta",
        string? mountPath = "/workspace",
        string lifecycle = "business",
        string? sandboxId = "sb-1")
        => new(claimName, ownerKind, ownerId, role, mountPath, lifecycle, sandboxId);

    [Fact]
    public void Registration_ValidItem_ReturnsNull()
        => Assert.Null(VolumeValidation.ValidateRegistration(Item()));

    [Theory]
    [InlineData("", "employee", "3f2a", "business")]
    [InlineData("pvc-1", "", "3f2a", "business")]
    [InlineData("pvc-1", "employee", "", "business")]
    [InlineData("pvc-1", "employee", "3f2a", "archive")]
    [InlineData("pvc-1", "employee", "3f2a", "")]
    public void Registration_InvalidItem_ReturnsMessage(
        string claimName, string ownerKind, string ownerId, string lifecycle)
        => Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(claimName, ownerKind, ownerId, lifecycle: lifecycle)));

    [Fact]
    public void Registration_FieldLimitsAreEnforced()
    {
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(ownerKind: new string('a', 33))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(ownerId: new string('a', 129))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(role: new string('a', 33))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(mountPath: "/" + new string('a', 128))));
        Assert.NotNull(VolumeValidation.ValidateRegistration(
            Item(sandboxId: new string('a', 129))));
    }

    [Fact]
    public void Registration_EmptyOptionalFields_AreAllowed()
        => Assert.Null(VolumeValidation.ValidateRegistration(
            Item(role: "", mountPath: "", sandboxId: "")));

    [Fact]
    public void Import_ValidItem_ReturnsNull()
    {
        var item = new VolumeImportItem(
            "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
            "meta", "/workspace", "business", "sb-1");
        Assert.Null(VolumeValidation.ValidateImportItem(item));
    }

    [Theory]
    [InlineData("Team", "opensandbox", "pvc-1")]
    [InlineData("goodcrew", "Team", "pvc-1")]
    [InlineData("goodcrew", "opensandbox", "-pvc")]
    [InlineData("goodcrew", "opensandbox", "pvc-1", "archive")]
    public void Import_InvalidItem_ReturnsMessage(
        string tenant, string namespaceName, string claimName, string lifecycle = "business")
    {
        var item = new VolumeImportItem(
            tenant, namespaceName, claimName, "employee", "3f2a",
            "meta", "/workspace", lifecycle, "sb-1");
        Assert.NotNull(VolumeValidation.ValidateImportItem(item));
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/ApiKeysTests.cs`：

```csharp
using System.Text.RegularExpressions;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ApiKeysTests
{
    [Fact]
    public void Create_Returns43UrlSafeCharacters()
    {
        for (var i = 0; i < 50; i++)
        {
            var key = ApiKeys.Create();
            Assert.Equal(43, key.Length);
            Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), key);
        }
    }

    [Fact]
    public void Create_IsRandom()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeys.Create()).ToHashSet();
        Assert.Equal(100, keys.Count);
    }

    [Fact]
    public void Hash_IsSha256AndDeterministic()
    {
        var key = ApiKeys.Create();
        Assert.Equal(32, ApiKeys.Hash(key).Length);
        Assert.Equal(ApiKeys.Hash(key), ApiKeys.Hash(key));
        Assert.NotEqual(ApiKeys.Hash(key), ApiKeys.Hash(ApiKeys.Create()));
    }

    [Fact]
    public void Prefix_IsFirstEightCharacters()
        => Assert.Equal("abcdefgh", ApiKeys.Prefix("abcdefghijklmnop"));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeValidationTests|FullyQualifiedName~ApiKeysTests"`

Expected: 编译失败，提示 DTO / `VolumeValidation` / `ApiKeys` 不存在。

- [ ] **Step 3: 实现契约**

新建 `backend/Contracts/VolumeContracts.cs`：

```csharp
namespace OpenSandbox.Web.Contracts;

public static class RegistrationStatuses
{
    public const string Registered = "registered";
    public const string Updated = "updated";
    public const string Reactivated = "reactivated";
    public const string Conflict = "conflict";
    public const string NotFound = "not-found";
    public const string Invalid = "invalid";
    public const string LabelPending = "label-pending";
}

public sealed record VolumeRegistrationRequest(
    string Namespace,
    IReadOnlyList<VolumeRegistrationItem> Volumes);

public sealed record VolumeRegistrationItem(
    string ClaimName,
    string OwnerKind,
    string OwnerId,
    string? Role,
    string? MountPath,
    string Lifecycle,
    string? SandboxId);

public sealed record VolumeRegistrationResult(
    string ClaimName,
    string Status,
    Guid? VolumeId,
    string? Message);

public sealed record VolumeRegistrationResponse(
    IReadOnlyList<VolumeRegistrationResult> Results);

public sealed record VolumeLifecycleRequest(string Lifecycle, string? Reason);

public sealed record VolumeLifecycleResponse(string Status, string LabelState);

public sealed record VolumeListItemDto(
    Guid? Id,
    string Namespace,
    string ClaimName,
    string? Tenant,
    string? TenantName,
    string? OwnerKind,
    string? OwnerId,
    string? Role,
    string? MountPath,
    string? Lifecycle,
    string? State,
    string? StateReason,
    string Classification,
    string? LabelState,
    IReadOnlyList<string> Violations,
    string? RegisteredUid,
    string? ObservedUid,
    DateTimeOffset? RegisteredAt,
    DateTimeOffset? LastSeenAt,
    bool? Mounted,
    string? StorageClass,
    string? Capacity,
    DateTimeOffset? CreatedAt);

public sealed record VolumeListResponse(
    IReadOnlyList<VolumeListItemDto> Items,
    PaginationInfoDto Pagination);

public sealed record VolumeDetailDto(
    VolumeListItemDto Volume,
    ClusterVolumeDto? Observed,
    IReadOnlyList<VolumeEventDto> Events);

public sealed record ClusterVolumeDto(
    string Namespace,
    string ClaimName,
    string Uid,
    string Phase,
    string? StorageClass,
    string? RequestedStorage,
    string? Capacity,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionTimestamp,
    bool Mounted,
    IReadOnlyList<string> MountedBy,
    IReadOnlyDictionary<string, string> Labels,
    string? LifecycleLabel,
    string? TenantLabel,
    string? OwnerLabel,
    string? ContractVersionLabel,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record VolumeEventDto(
    Guid Id,
    Guid? VolumeId,
    string Namespace,
    string ClaimName,
    string? TenantId,
    string Type,
    string Actor,
    string? Reason,
    string DataJson,
    DateTimeOffset CreatedAt);

public sealed record VolumeReportCounts(
    int Observed,
    int Registered,
    int Unlabeled,
    int Unregistered,
    int Drift,
    int Missing,
    int Reappeared,
    int Unmanaged);

public sealed record VolumeCoverageDto(
    int Observed,
    int Labeled,
    int Registered,
    decimal LabeledPercent,
    decimal RegisteredPercent);

public sealed record VolumeDistributionItem(string Key, int Count);

public sealed record VolumeViolationDto(
    string Type,
    string Namespace,
    string ClaimName,
    string Message);

public sealed record ScanErrorDto(string Namespace, string Message);

public sealed record ScanRunDto(
    Guid Id,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanErrorDto> Errors);

public sealed record VolumeScanResponse(
    Guid ScanId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanErrorDto> Errors);

public sealed record VolumeReportDto(
    ScanRunDto? LastScan,
    VolumeReportCounts Counts,
    VolumeCoverageDto Coverage,
    IReadOnlyList<VolumeDistributionItem> ByTenant,
    IReadOnlyList<VolumeDistributionItem> ByNamespace,
    IReadOnlyList<VolumeViolationDto> Violations,
    bool Truncated);

public sealed record VolumeImportRequest(
    bool? DryRun,
    IReadOnlyList<VolumeImportItem> Items);

public sealed record VolumeImportItem(
    string Tenant,
    string Namespace,
    string ClaimName,
    string OwnerKind,
    string OwnerId,
    string? Role,
    string? MountPath,
    string Lifecycle,
    string? SandboxId);

public sealed record VolumeImportResult(
    string Namespace,
    string ClaimName,
    string Status,
    string? Message);

public sealed record VolumeImportResponse(
    bool DryRun,
    IReadOnlyList<VolumeImportResult> Results);

public sealed record VolumeTenantDto(
    string Id,
    string DisplayName,
    string Status,
    IReadOnlyList<string> Namespaces,
    int VolumeCount,
    int ProtectedCount,
    int EphemeralCount,
    int MissingCount,
    int KeyCount,
    DateTimeOffset CreatedAt);

public sealed record TenantListResponse(IReadOnlyList<VolumeTenantDto> Items);

public sealed record CreateTenantRequest(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Namespaces);

public sealed record UpdateTenantRequest(
    string? DisplayName,
    string? Status,
    IReadOnlyList<string>? Namespaces);

public sealed record TenantKeyDto(
    Guid Id,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record TenantKeyListResponse(IReadOnlyList<TenantKeyDto> Items);

public sealed record CreateTenantKeyRequest(string? Name);

public sealed record CreateTenantKeyResponse(Guid Id, string Key, string Prefix);
```

在 `backend/Infrastructure/Volumes/VolumeValues.cs` 追加：

```csharp
public static class VolumeClassifications
{
    public const string Registered = "registered";
    public const string Unregistered = "unregistered";
    public const string Unlabeled = "unlabeled";
    public const string Drift = "drift";
    public const string Missing = "missing";
    public const string Reappeared = "reappeared";
    public const string Unmanaged = "unmanaged";
}
```

新建 `backend/Infrastructure/Volumes/VolumeValidation.cs`：

```csharp
using System.Text.RegularExpressions;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeValidation
{
    public const int OwnerKindMax = 32;
    public const int OwnerIdMax = 128;
    public const int RoleMax = 32;
    public const int MountPathMax = 128;
    public const int SandboxIdMax = 128;
    public const int ReasonMax = 512;

    private static readonly Regex TenantIdPattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly Regex DnsLabelPattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly Regex ClaimNamePattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$",
            RegexOptions.Compiled);

    public static string? TenantId(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 63 } && TenantIdPattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? Namespace(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 63 } && DnsLabelPattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? ClaimName(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= 253 } && ClaimNamePattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }

    public static string? Text(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max)
            return null;
        return trimmed;
    }

    public static string? ValidateRegistration(VolumeRegistrationItem item)
    {
        if (ClaimName(item.ClaimName) is null)
            return "claimName 不合法";
        if (Text(item.OwnerKind, OwnerKindMax) is null)
            return "ownerKind 为空或超过 32 字符";
        if (Text(item.OwnerId, OwnerIdMax) is null)
            return "ownerId 为空或超过 128 字符";
        if (!string.IsNullOrWhiteSpace(item.Role) && Text(item.Role, RoleMax) is null)
            return "role 超过 32 字符";
        if (!string.IsNullOrWhiteSpace(item.MountPath) && Text(item.MountPath, MountPathMax) is null)
            return "mountPath 超过 128 字符";
        if (!VolumeLifecycle.IsValid(item.Lifecycle?.Trim()))
            return "lifecycle 必须为 business 或 ephemeral";
        if (!string.IsNullOrWhiteSpace(item.SandboxId) && Text(item.SandboxId, SandboxIdMax) is null)
            return "sandboxId 超过 128 字符";
        return null;
    }

    public static string? ValidateImportItem(VolumeImportItem item)
    {
        if (TenantId(item.Tenant) is null)
            return "tenant 不合法";
        if (Namespace(item.Namespace) is null)
            return "namespace 不合法";
        var registration = ValidateRegistration(new VolumeRegistrationItem(
            item.ClaimName, item.OwnerKind, item.OwnerId,
            item.Role, item.MountPath, item.Lifecycle, item.SandboxId));
        return registration;
    }
}
```

新建 `backend/Infrastructure/Volumes/ApiKeys.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class ApiKeys
{
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static string Prefix(string key) => key[..Math.Min(8, key.Length)];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeValidationTests|FullyQualifiedName~ApiKeysTests"`

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend/Contracts/VolumeContracts.cs backend/Infrastructure/Volumes/VolumeValues.cs backend/Infrastructure/Volumes/VolumeValidation.cs backend/Infrastructure/Volumes/ApiKeys.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeValidationTests.cs tests/OpenSandbox.Web.Tests/Volumes/ApiKeysTests.cs
git commit -m "feat(volumes): add API contracts and key primitives" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 租户 Key 认证与异常映射

**Files:**

- Create: `backend/Infrastructure/Volumes/VolumeExceptions.cs`
- Create: `backend/Infrastructure/Volumes/ApiKeyAuthenticator.cs`
- Create: `backend/Host/Web/VolumeExceptionMiddleware.cs`
- Create: `backend/Host/Security/TenantApiKeyMiddleware.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/ApiKeyAuthenticatorTests.cs`

**Interfaces:**

- Consumes: Task 1 `ApiKeys`
- Produces: `VolumeDependencyException`、`VolumeConflictException`、`VolumeNotFoundException`、`VolumeValidationException`、`VolumeUnauthorizedException`
- Produces: `VolumeExceptionMiddleware`（映射 400/401/404/409/503，含 `ScanInProgressException`）
- Produces: `TenantIdentity(string TenantId, string DisplayName)`
- Produces: `ApiKeyAuthenticator.AuthenticateAsync(string? rawKey, CancellationToken): Task<TenantIdentity?>`
- Produces: `TenantApiKeyMiddleware`，为租户请求写入 `tenant`、`tenant_name`、`sub`、`auth_method` claims

- [ ] **Step 1: 写认证失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/ApiKeyAuthenticatorTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ApiKeyAuthenticatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, string RawKey)> SeedAsync(
        string status = "active", bool revoked = false)
    {
        var test = new TestDb();
        var tenant = await test.Db.Tenants.SingleAsync(t => t.Id == "goodcrew");
        tenant.Status = status;
        var raw = ApiKeys.Create();
        test.Db.TenantApiKeys.Add(new TenantApiKeyEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = "goodcrew",
            Name = "default",
            KeyHash = ApiKeys.Hash(raw),
            Prefix = ApiKeys.Prefix(raw),
            CreatedAt = Now,
            RevokedAt = revoked ? Now : null,
        });
        await test.Db.SaveChangesAsync();
        return (test, raw);
    }

    [Fact]
    public async Task ValidKey_ReturnsTenantIdentity()
    {
        var (test, raw) = await SeedAsync();
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        var identity = await authenticator.AuthenticateAsync(raw, CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("goodcrew", identity!.TenantId);
        var stored = await test.Db.TenantApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(Now, stored.LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    public async Task MissingOrUnknownKey_ReturnsNull(string? raw)
    {
        var (test, _) = await SeedAsync();
        using var __ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task RevokedKey_ReturnsNull()
    {
        var (test, raw) = await SeedAsync(revoked: true);
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledTenant_ReturnsNull()
    {
        var (test, raw) = await SeedAsync(status: "disabled");
        using var _ = test;
        var authenticator = new ApiKeyAuthenticator(test.Db, new FixedTimeProvider(Now));

        Assert.Null(await authenticator.AuthenticateAsync(raw, CancellationToken.None));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ApiKeyAuthenticatorTests"`

Expected: 编译失败，提示 `ApiKeyAuthenticator` / 异常类型不存在。

- [ ] **Step 3: 实现异常、认证器与中间件**

新建 `backend/Infrastructure/Volumes/VolumeExceptions.cs`：

```csharp
namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class VolumeDependencyException(string message) : Exception(message);

public sealed class VolumeConflictException(string message) : Exception(message);

public sealed class VolumeNotFoundException(string message) : Exception(message);

public sealed class VolumeValidationException(string message) : Exception(message);

public sealed class VolumeUnauthorizedException(string message) : Exception(message);
```

新建 `backend/Infrastructure/Volumes/ApiKeyAuthenticator.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record TenantIdentity(string TenantId, string DisplayName);

public sealed class ApiKeyAuthenticator(VolumeDbContext db, TimeProvider time)
{
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    public async Task<TenantIdentity?> AuthenticateAsync(string? rawKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var hash = ApiKeys.Hash(rawKey);
        var key = await db.TenantApiKeys
            .AsNoTracking()
            .Include(entity => entity.Tenant)
            .SingleOrDefaultAsync(entity =>
                entity.KeyHash == hash &&
                entity.RevokedAt == null &&
                entity.Tenant.Status == "active", ct);

        if (key is null)
            return null;

        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt >= LastUsedThrottle)
        {
            await db.TenantApiKeys
                .Where(entity => entity.Id == key.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(entity => entity.LastUsedAt, now), ct);
        }

        return new TenantIdentity(key.TenantId, key.Tenant.DisplayName);
    }
}
```

新建 `backend/Host/Web/VolumeExceptionMiddleware.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public sealed class VolumeExceptionMiddleware(
    RequestDelegate next,
    ILogger<VolumeExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (VolumeDependencyException exception)
        {
            await WriteAsync(context, 503, "dependency_unavailable", exception.Message);
        }
        catch (VolumeUnauthorizedException exception)
        {
            await WriteAsync(context, 401, "unauthorized", exception.Message);
        }
        catch (VolumeValidationException exception)
        {
            await WriteAsync(context, 400, "invalid_argument", exception.Message);
        }
        catch (VolumeNotFoundException exception)
        {
            await WriteAsync(context, 404, "not_found", exception.Message);
        }
        catch (VolumeConflictException exception)
        {
            await WriteAsync(context, 409, "conflict", exception.Message);
        }
        catch (ScanInProgressException exception)
        {
            await WriteAsync(context, 409, "scan_in_progress", exception.Message);
        }
    }

    private async Task WriteAsync(
        HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            logger.LogWarning("响应已开始，无法写入卷治理错误 {Code}", code);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(code, message));
    }
}
```

新建 `backend/Host/Security/TenantApiKeyMiddleware.cs`：

```csharp
using System.Security.Claims;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Security;

public sealed class TenantApiKeyMiddleware(
    RequestDelegate next,
    ILogger<TenantApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyAuthenticator authenticator)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/tenant"))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var rawKey = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

        TenantIdentity? identity;
        try
        {
            identity = await authenticator.AuthenticateAsync(rawKey, context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "租户 API Key 校验失败");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("dependency_unavailable", "认证服务暂不可用"));
            return;
        }

        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("unauthorized", "缺少或无效的租户 API Key"));
            return;
        }

        var claims = new[]
        {
            new Claim("tenant", identity.TenantId),
            new Claim("tenant_name", identity.DisplayName),
            new Claim("sub", $"tenant:{identity.TenantId}"),
            new Claim("auth_method", "tenant-key"),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TenantApiKey"));
        await next(context);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~ApiKeyAuthenticatorTests"`

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend/Infrastructure/Volumes/VolumeExceptions.cs backend/Infrastructure/Volumes/ApiKeyAuthenticator.cs backend/Host/Web/VolumeExceptionMiddleware.cs backend/Host/Security/TenantApiKeyMiddleware.cs tests/OpenSandbox.Web.Tests/Volumes/ApiKeyAuthenticatorTests.cs
git commit -m "feat(volumes): add tenant key authentication and error mapping" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 租户与 Key 管理服务

**Files:**

- Create: `backend/Infrastructure/Volumes/TenantService.cs`
- Create: `backend/Host/Web/VolumeTenantEndpoints.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/TenantServiceTests.cs`

**Interfaces:**

- Consumes: Task 1 `ApiKeys` / `VolumeValidation` / 契约；Task 2 异常
- Produces: `TenantService.ListAsync`、`CreateAsync`、`UpdateAsync`、`ListKeysAsync`、`CreateKeyAsync`、`RevokeKeyAsync`
- Produces: `/api/v1/volume-tenants` 六个端点

- [ ] **Step 1: 写租户服务失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/TenantServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class TenantServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static TenantService Service(TestDb test) =>
        new(test.Db, new FixedTimeProvider(Now));

    [Fact]
    public async Task Create_AddsTenantAndNamespaces()
    {
        using var test = new TestDb();

        var created = await Service(test).CreateAsync(
            new CreateTenantRequest("team-a", "Team A", ["team-a-ns"]), CancellationToken.None);

        Assert.Equal("team-a", created.Id);
        Assert.Equal(["team-a-ns"], created.Namespaces);
        Assert.True(await test.Db.TenantNamespaces.AnyAsync(n => n.Namespace == "team-a-ns"));
    }

    [Fact]
    public async Task Create_DuplicateId_ThrowsConflict()
    {
        using var test = new TestDb();

        await Assert.ThrowsAsync<VolumeConflictException>(() => Service(test).CreateAsync(
            new CreateTenantRequest("goodcrew", "Again", ["ns-1"]), CancellationToken.None));
    }

    [Fact]
    public async Task Create_NamespaceAlreadyAssigned_ThrowsConflict()
    {
        using var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox", TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<VolumeConflictException>(() => Service(test).CreateAsync(
            new CreateTenantRequest("team-b", "Team B", ["opensandbox"]), CancellationToken.None));
    }

    [Theory]
    [InlineData("Team")]
    [InlineData("team_b")]
    [InlineData("")]
    public async Task Create_InvalidTenantId_ThrowsValidation(string id)
    {
        using var test = new TestDb();

        await Assert.ThrowsAsync<VolumeValidationException>(() => Service(test).CreateAsync(
            new CreateTenantRequest(id, "Team", ["ns-1"]), CancellationToken.None));
    }

    [Fact]
    public async Task Update_ReplacesNamespacesWithoutDuplicatingKeys()
    {
        using var test = new TestDb();
        var service = Service(test);
        await service.CreateAsync(
            new CreateTenantRequest("team-a", "Team A", ["ns-a", "ns-b"]), CancellationToken.None);

        var updated = await service.UpdateAsync(
            "team-a", new UpdateTenantRequest("Team A2", "active", ["ns-b", "ns-c"]),
            CancellationToken.None);

        Assert.Equal("Team A2", updated.DisplayName);
        Assert.Equal(["ns-b", "ns-c"], updated.Namespaces.OrderBy(x => x));
        Assert.Equal(2, await test.Db.TenantNamespaces.CountAsync(n => n.TenantId == "team-a"));
    }

    [Fact]
    public async Task Update_DisabledStatus_IsPersisted()
    {
        using var test = new TestDb();

        var updated = await Service(test).UpdateAsync(
            "goodcrew", new UpdateTenantRequest(null, "disabled", null), CancellationToken.None);

        Assert.Equal("disabled", updated.Status);
    }

    [Fact]
    public async Task CreateKey_ReturnsPlaintextOnceAndStoresHash()
    {
        using var test = new TestDb();
        var service = Service(test);

        var created = await service.CreateKeyAsync(
            "goodcrew", new CreateTenantKeyRequest("ci"), CancellationToken.None);

        Assert.Equal(43, created.Key.Length);
        Assert.Equal(created.Key[..8], created.Prefix);
        var stored = await test.Db.TenantApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(ApiKeys.Hash(created.Key), stored.KeyHash);
        Assert.DoesNotContain(created.Key, Convert.ToBase64String(stored.KeyHash));

        var listed = await service.ListKeysAsync("goodcrew", CancellationToken.None);
        Assert.DoesNotContain(created.Key, listed[0].Prefix);
    }

    [Fact]
    public async Task RevokeKey_IsIdempotentAndScopedToTenant()
    {
        using var test = new TestDb();
        var service = Service(test);
        var created = await service.CreateKeyAsync(
            "goodcrew", new CreateTenantKeyRequest("ci"), CancellationToken.None);

        await service.RevokeKeyAsync("goodcrew", created.Id, CancellationToken.None);
        await service.RevokeKeyAsync("goodcrew", created.Id, CancellationToken.None);

        Assert.NotNull((await test.Db.TenantApiKeys.AsNoTracking().SingleAsync()).RevokedAt);
        await Assert.ThrowsAsync<VolumeNotFoundException>(() =>
            service.RevokeKeyAsync("other", created.Id, CancellationToken.None));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~TenantServiceTests"`

Expected: 编译失败，提示 `TenantService` 不存在。

- [ ] **Step 3: 实现租户服务**

新建 `backend/Infrastructure/Volumes/TenantService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class TenantService(VolumeDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<VolumeTenantDto>> ListAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Include(tenant => tenant.Namespaces)
            .OrderBy(tenant => tenant.Id)
            .ToListAsync(ct);
        var volumes = await db.Volumes.AsNoTracking().ToListAsync(ct);
        var keys = await db.TenantApiKeys.AsNoTracking()
            .Where(key => key.RevokedAt == null)
            .GroupBy(key => key.TenantId)
            .Select(group => new { TenantId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.TenantId, row => row.Count, ct);

        return tenants
            .Select(tenant => ToDto(
                tenant,
                volumes.Where(volume => volume.TenantId == tenant.Id).ToList(),
                keys.GetValueOrDefault(tenant.Id)))
            .ToArray();
    }

    public async Task<VolumeTenantDto> CreateAsync(
        CreateTenantRequest request, CancellationToken ct)
    {
        var id = VolumeValidation.TenantId(request.Id)
            ?? throw new VolumeValidationException("租户 ID 必须为小写 DNS 标签，最长 63 字符");
        var displayName = VolumeValidation.Text(request.DisplayName, 200)
            ?? throw new VolumeValidationException("显示名称不能为空且不超过 200 字符");
        var namespaces = NormalizeNamespaces(request.Namespaces);

        if (await db.Tenants.AnyAsync(tenant => tenant.Id == id, ct))
            throw new VolumeConflictException("租户已存在");

        var conflicts = await db.TenantNamespaces
            .Where(mapping => namespaces.Contains(mapping.Namespace))
            .Select(mapping => mapping.Namespace)
            .ToListAsync(ct);
        if (conflicts.Count > 0)
            throw new VolumeConflictException($"命名空间已分配给其他租户: {string.Join(", ", conflicts)}");

        var now = time.GetUtcNow();
        var tenant = new TenantEntity
        {
            Id = id,
            DisplayName = displayName,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
            Namespaces = namespaces.Select(namespaceName => new TenantNamespaceEntity
            {
                Namespace = namespaceName,
                TenantId = id,
            }).ToList(),
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<VolumeTenantDto> UpdateAsync(
        string tenantId, UpdateTenantRequest request, CancellationToken ct)
    {
        var tenant = await db.Tenants
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity => entity.Id == tenantId, ct)
            ?? throw new VolumeNotFoundException("租户不存在");

        if (request.DisplayName is not null)
        {
            tenant.DisplayName = VolumeValidation.Text(request.DisplayName, 200)
                ?? throw new VolumeValidationException("显示名称不能为空且不超过 200 字符");
        }

        if (request.Status is not null)
        {
            if (request.Status is not ("active" or "disabled"))
                throw new VolumeValidationException("status 必须为 active 或 disabled");
            tenant.Status = request.Status;
        }

        if (request.Namespaces is not null)
        {
            var next = NormalizeNamespaces(request.Namespaces);
            var conflicts = await db.TenantNamespaces
                .Where(mapping =>
                    next.Contains(mapping.Namespace) && mapping.TenantId != tenantId)
                .Select(mapping => mapping.Namespace)
                .ToListAsync(ct);
            if (conflicts.Count > 0)
                throw new VolumeConflictException($"命名空间已分配给其他租户: {string.Join(", ", conflicts)}");

            var existing = tenant.Namespaces.Select(mapping => mapping.Namespace).ToHashSet();
            db.TenantNamespaces.RemoveRange(
                tenant.Namespaces.Where(mapping => !next.Contains(mapping.Namespace)));
            foreach (var namespaceName in next.Where(name => !existing.Contains(name)))
            {
                tenant.Namespaces.Add(new TenantNamespaceEntity
                {
                    Namespace = namespaceName,
                    TenantId = tenantId,
                });
            }
        }

        tenant.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, ct);
    }

    public async Task<IReadOnlyList<TenantKeyDto>> ListKeysAsync(
        string tenantId, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        return await db.TenantApiKeys.AsNoTracking()
            .Where(key => key.TenantId == tenantId)
            .OrderByDescending(key => key.CreatedAt)
            .Select(key => new TenantKeyDto(
                key.Id, key.Name, key.Prefix, key.CreatedAt, key.RevokedAt, key.LastUsedAt))
            .ToListAsync(ct);
    }

    public async Task<CreateTenantKeyResponse> CreateKeyAsync(
        string tenantId, CreateTenantKeyRequest request, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? "默认 Key"
            : VolumeValidation.Text(request.Name, 200)
              ?? throw new VolumeValidationException("Key 名称不超过 200 字符");

        var raw = ApiKeys.Create();
        var entity = new TenantApiKeyEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            KeyHash = ApiKeys.Hash(raw),
            Prefix = ApiKeys.Prefix(raw),
            CreatedAt = time.GetUtcNow(),
        };
        db.TenantApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        return new CreateTenantKeyResponse(entity.Id, raw, entity.Prefix);
    }

    public async Task RevokeKeyAsync(string tenantId, Guid keyId, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        var key = await db.TenantApiKeys.SingleOrDefaultAsync(
            entity => entity.Id == keyId && entity.TenantId == tenantId, ct)
            ?? throw new VolumeNotFoundException("Key 不存在");

        if (key.RevokedAt is null)
        {
            key.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<VolumeTenantDto> GetAsync(string tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity => entity.Id == tenantId, ct)
            ?? throw new VolumeNotFoundException("租户不存在");
        var volumes = await db.Volumes.AsNoTracking()
            .Where(volume => volume.TenantId == tenantId)
            .ToListAsync(ct);
        var keyCount = await db.TenantApiKeys.AsNoTracking()
            .CountAsync(key => key.TenantId == tenantId && key.RevokedAt == null, ct);
        return ToDto(tenant, volumes, keyCount);
    }

    private async Task EnsureTenantAsync(string tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
            throw new VolumeNotFoundException("租户不存在");
    }

    private static List<string> NormalizeNamespaces(IReadOnlyList<string>? namespaces)
    {
        if (namespaces is null)
            return [];

        var result = new List<string>();
        foreach (var value in namespaces)
        {
            var namespaceName = VolumeValidation.Namespace(value)
                ?? throw new VolumeValidationException($"命名空间不合法: {value}");
            if (!result.Contains(namespaceName, StringComparer.Ordinal))
                result.Add(namespaceName);
        }
        return result;
    }

    private static VolumeTenantDto ToDto(
        TenantEntity tenant,
        IReadOnlyList<VolumeEntity> volumes,
        int keyCount) => new(
        tenant.Id,
        tenant.DisplayName,
        tenant.Status,
        tenant.Namespaces.Select(mapping => mapping.Namespace).OrderBy(name => name).ToArray(),
        volumes.Count,
        volumes.Count(volume => volume.Lifecycle == VolumeLifecycle.Business),
        volumes.Count(volume => volume.Lifecycle == VolumeLifecycle.Ephemeral),
        volumes.Count(volume => volume.State == VolumeStates.Missing),
        keyCount,
        tenant.CreatedAt);
}
```

- [ ] **Step 4: 实现租户管理端点**

新建 `backend/Host/Web/VolumeTenantEndpoints.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeTenantEndpoints
{
    public static IEndpointRouteBuilder MapVolumeTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/volume-tenants");

        group.MapGet("", async (TenantService service, CancellationToken ct) =>
        {
            var items = await service.ListAsync(ct);
            return Results.Ok(new TenantListResponse(items));
        });

        group.MapPost("", async (
            CreateTenantRequest request, TenantService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/volume-tenants/{created.Id}", created);
        });

        group.MapPatch("/{tenantId}", async (
            string tenantId, UpdateTenantRequest request,
            TenantService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.UpdateAsync(tenantId, request, ct));
        });

        group.MapGet("/{tenantId}/keys", async (
            string tenantId, TenantService service, CancellationToken ct) =>
        {
            var items = await service.ListKeysAsync(tenantId, ct);
            return Results.Ok(new TenantKeyListResponse(items));
        });

        group.MapPost("/{tenantId}/keys", async (
            string tenantId, CreateTenantKeyRequest request,
            TenantService service, CancellationToken ct) =>
        {
            var created = await service.CreateKeyAsync(tenantId, request, ct);
            return Results.Created($"/api/v1/volume-tenants/{tenantId}/keys/{created.Id}", created);
        });

        group.MapPost("/{tenantId}/keys/{keyId:guid}/revoke", async (
            string tenantId, Guid keyId, TenantService service, CancellationToken ct) =>
        {
            await service.RevokeKeyAsync(tenantId, keyId, ct);
            return Results.NoContent();
        });

        return app;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~TenantServiceTests"`

Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add backend/Infrastructure/Volumes/TenantService.cs backend/Host/Web/VolumeTenantEndpoints.cs tests/OpenSandbox.Web.Tests/Volumes/TenantServiceTests.cs
git commit -m "feat(volumes): add tenant and key management" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 卷注册、生命周期与租户端点

**Files:**

- Create: `backend/Infrastructure/Volumes/RegistrationService.cs`
- Create: `backend/Infrastructure/Volumes/LifecycleService.cs`
- Create: `backend/Host/Web/VolumeAgentEndpoints.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/RegistrationServiceTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/LifecycleServiceTests.cs`

**Interfaces:**

- Consumes: Task 1 契约/校验、Task 2 异常、Task 3 `TenantService`（仅测试准备）
- Produces: `RegistrationService.RegisterAsync(string tenantId, VolumeRegistrationRequest request, string actor, CancellationToken): Task<VolumeRegistrationResponse>`
- Produces: `LifecycleService.ChangeAsync(string? tenantId, string namespaceName, string claimName, VolumeLifecycleRequest request, bool isAdmin, string actor, CancellationToken): Task<VolumeLifecycleResponse>`
- Produces: `/api/v1/tenant/volumes/register`、`/api/v1/tenant/volumes/{namespace}/{claimName}/lifecycle`

- [ ] **Step 1: 写注册与生命周期失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/RegistrationServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class RegistrationServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static RegistrationService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, kube, new VolumeLabelWriter(kube),
            new FixedTimeProvider(Now), NullLogger<RegistrationService>.Instance);

    private static async Task SeedNamespaceAsync(TestDb test)
    {
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
    }

    private static VolumeRegistrationRequest Request(
        string claimName = "pvc-1",
        string ownerKind = "employee",
        string ownerId = "3f2a",
        string lifecycle = "business",
        string namespaceName = "opensandbox")
        => new(namespaceName,
        [
            new VolumeRegistrationItem(
                claimName, ownerKind, ownerId, "meta", "/workspace", lifecycle, "sb-1"),
        ]);

    [Fact]
    public async Task Register_NewVolume_CreatesLedgerAndAppliesLabels()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal(RegistrationStatuses.Registered, result.Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal("goodcrew", volume.TenantId);
        Assert.Equal("uid-1", volume.RegisteredUid);
        Assert.Equal(VolumeStates.Active, volume.State);
        Assert.Equal(VolumeLabelStates.Applied, volume.LabelState);
        Assert.Single(kube.Patches);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e => e.Type == VolumeEventTypes.Registered));
    }

    [Fact]
    public async Task Register_SameOwnerTwice_UpdatesWithoutSecondRow()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        var service = Service(test, kube);

        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);
        var second = await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Updated, second.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_MissingPvc_ReturnsNotFoundWithoutRow()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.NotFound, response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_ClaimOwnedByOtherTenant_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "other", OwnerKind = "employee", OwnerId = "other",
            Lifecycle = VolumeLifecycle.Business, RegisteredAt = Now, UpdatedAt = Now,
            RegisteredUid = "uid-1", State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal("other", (await test.Db.Volumes.SingleAsync()).TenantId);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.RegistrationConflict));
    }

    [Fact]
    public async Task Register_SameOwnerDifferentClaim_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request("pvc-1"), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew", Request("pvc-2"), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_SameOwnerInAnotherNamespace_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox-2",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.Namespaces["opensandbox-2"] = new NamespaceInfo(
            "opensandbox-2",
            new Dictionary<string, string> { [VolumeLabels.Managed] = "true" });
        kube.AddPvc("opensandbox", "pvc-1");
        kube.AddPvc("opensandbox-2", "pvc-2", uid: "uid-2");
        var service = Service(test, kube);
        await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew",
            Request("pvc-2", namespaceName: "opensandbox-2"),
            "tenant:goodcrew",
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_LifecycleMismatch_ReturnsConflict()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var response = await service.RegisterAsync(
            "goodcrew", Request(lifecycle: "ephemeral"), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(VolumeLifecycle.Business, (await test.Db.Volumes.SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task Register_LabelPatchFailure_StillWritesLedger()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");
        kube.FailLabelPatches.Add("pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.LabelPending, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLabelStates.Pending, volume.LabelState);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.LabelPatchFailed));
    }

    [Fact]
    public async Task Register_NamespaceNotAssigned_ReturnsPerItemInvalid()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Register_NamespaceWithoutManagedLabel_ReturnsPerItemInvalid()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = new FakeVolumeKubernetesClient();
        kube.Namespaces["opensandbox"] = new NamespaceInfo(
            "opensandbox", new Dictionary<string, string>());
        kube.AddPvc("opensandbox", "pvc-1");

        var response = await Service(test, kube).RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
    }

    [Fact]
    public async Task Register_ReactivateMissingVolume_ReturnsReactivated()
    {
        using var test = new TestDb();
        await SeedNamespaceAsync(test);
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        var service = Service(test, kube);
        await service.RegisterAsync("goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        volume.State = VolumeStates.Missing;
        volume.StateReason = VolumeStates.Unexpected;
        await test.Db.SaveChangesAsync();

        var response = await service.RegisterAsync(
            "goodcrew", Request(), "tenant:goodcrew", CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Reactivated, response.Results[0].Status);
        Assert.Equal(VolumeStates.Active, (await test.Db.Volumes.SingleAsync()).State);
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/LifecycleServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class LifecycleServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, FakeVolumeKubernetesClient Kube)> SeedAsync()
    {
        var test = new TestDb();
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(),
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            TenantId = "goodcrew",
            OwnerKind = "employee",
            OwnerId = "3f2a",
            Lifecycle = VolumeLifecycle.Business,
            RegisteredAt = Now,
            UpdatedAt = Now,
            RegisteredUid = "uid-1",
            State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        return (test, kube);
    }

    private static LifecycleService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, new VolumeLabelWriter(kube),
            new FixedTimeProvider(Now), NullLogger<LifecycleService>.Instance);

    [Fact]
    public async Task DowngradeWithoutReason_ThrowsValidation()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        await Assert.ThrowsAsync<VolumeValidationException>(() => Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", null), false,
            "tenant:goodcrew", CancellationToken.None));
    }

    [Fact]
    public async Task DowngradeWithReason_ChangesLedgerAndOnlyLifecycleLabel()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "sandbox 已完成"), false,
            "tenant:goodcrew", CancellationToken.None);

        Assert.Equal("updated", response.Status);
        Assert.Equal(VolumeLabelStates.Applied, response.LabelState);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLifecycle.Ephemeral, volume.Lifecycle);
        Assert.Equal(["opensandbox.io/volume-lifecycle"], kube.Patches[0].Labels.Keys);
        var audit = await test.Db.VolumeEvents.SingleAsync(
            e => e.Type == VolumeEventTypes.LifecycleChanged);
        Assert.Equal("sandbox 已完成", audit.Reason);
    }

    [Fact]
    public async Task OtherTenant_CannotChangeLifecycle()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        await Assert.ThrowsAsync<VolumeNotFoundException>(() => Service(test, kube).ChangeAsync(
            "other", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "cleanup"), false,
            "tenant:other", CancellationToken.None));
    }

    [Fact]
    public async Task Admin_CanChangeAnyTenant()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            null, "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("ephemeral", "admin cleanup"), true,
            "admin", CancellationToken.None);

        Assert.Equal("updated", response.Status);
    }

    [Fact]
    public async Task SameLifecycle_ReturnsUnchanged()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ChangeAsync(
            "goodcrew", "opensandbox", "pvc-1",
            new VolumeLifecycleRequest("business", null), false,
            "tenant:goodcrew", CancellationToken.None);

        Assert.Equal("unchanged", response.Status);
        Assert.Empty(kube.Patches);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~RegistrationServiceTests|FullyQualifiedName~LifecycleServiceTests"`

Expected: 编译失败，提示 `RegistrationService` / `LifecycleService` 不存在。

- [ ] **Step 3: 实现注册服务**

新建 `backend/Infrastructure/Volumes/RegistrationService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class RegistrationService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    TimeProvider time,
    ILogger<RegistrationService> logger)
{
    public async Task<VolumeRegistrationResponse> RegisterAsync(
        string tenantId,
        VolumeRegistrationRequest request,
        string actor,
        CancellationToken ct)
    {
        if (request.Volumes is null || request.Volumes.Count == 0)
            throw new VolumeValidationException("volumes 不能为空");

        var namespaceName = VolumeValidation.Namespace(request.Namespace)
            ?? throw new VolumeValidationException("namespace 不合法");

        var tenant = await db.Tenants.AsNoTracking()
            .Include(entity => entity.Namespaces)
            .SingleOrDefaultAsync(entity =>
                entity.Id == tenantId && entity.Status == "active", ct)
            ?? throw new VolumeUnauthorizedException("租户不存在或已停用");

        if (!tenant.Namespaces.Any(mapping => mapping.Namespace == namespaceName))
            return InvalidAll(request, "命名空间未分配给该租户");

        NamespaceInfo? namespaceInfo;
        try
        {
            namespaceInfo = await kube.GetNamespaceAsync(namespaceName, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "读取命名空间 {Namespace} 失败", namespaceName);
            throw new VolumeDependencyException("Kubernetes 暂不可用");
        }

        if (namespaceInfo is null ||
            !namespaceInfo.Labels.TryGetValue(VolumeLabels.Managed, out var managed) ||
            managed != "true")
            return InvalidAll(request, "命名空间未标记为受管");

        IReadOnlyList<ObservedPvc> pvcs;
        try
        {
            pvcs = await kube.ListPvcsAsync(namespaceName, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "列出命名空间 {Namespace} 的 PVC 失败", namespaceName);
            throw new VolumeDependencyException("Kubernetes 暂不可用");
        }

        var pvcIndex = pvcs.ToDictionary(pvc => pvc.ClaimName, StringComparer.Ordinal);
        var claimNames = request.Volumes
            .Select(item => item.ClaimName?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rowsByClaim = await db.Volumes
            .Where(volume => volume.Namespace == namespaceName && claimNames.Contains(volume.ClaimName))
            .ToDictionaryAsync(volume => volume.ClaimName, StringComparer.Ordinal, ct);
        // owner 唯一性按租户跨命名空间判定，避免同一 owner 在不同命名空间产生第二行台账。
        var tenantRows = await db.Volumes
            .Where(volume => volume.TenantId == tenantId)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var results = new List<VolumeRegistrationResult>();
        var toPatch = new List<VolumeEntity>();
        var seenClaims = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in request.Volumes)
        {
            var claimName = item.ClaimName?.Trim() ?? "";
            var validation = VolumeValidation.ValidateRegistration(item);
            if (validation is not null)
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Invalid, null, validation));
                continue;
            }

            if (!seenClaims.Add(claimName))
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Invalid, null, "请求中存在重复的 claimName"));
                continue;
            }

            if (!pvcIndex.TryGetValue(claimName, out var pvc))
            {
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.NotFound, null, "PVC 不存在"));
                continue;
            }

            var ownerKind = item.OwnerKind.Trim();
            var ownerId = item.OwnerId.Trim();
            var lifecycle = item.Lifecycle.Trim();
            var byClaim = rowsByClaim.GetValueOrDefault(claimName);

            if (byClaim is not null &&
                (byClaim.TenantId != tenantId ||
                 byClaim.OwnerKind != ownerKind ||
                 byClaim.OwnerId != ownerId))
            {
                await AddConflictAsync(byClaim, actor, now, "卷已被其他租户或 owner 注册", ct);
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Conflict, byClaim.Id,
                    "卷已被其他租户或 owner 注册"));
                continue;
            }

            var ownerRow = tenantRows.FirstOrDefault(volume =>
                (volume.Namespace != namespaceName || volume.ClaimName != claimName) &&
                volume.OwnerKind == ownerKind &&
                volume.OwnerId == ownerId);
            if (ownerRow is not null)
            {
                await AddConflictAsync(ownerRow, actor, now, "owner 已绑定其他 PVC", ct);
                results.Add(new VolumeRegistrationResult(
                    claimName, RegistrationStatuses.Conflict, ownerRow.Id,
                    "owner 已绑定其他 PVC"));
                continue;
            }

            if (byClaim is not null)
            {
                if (byClaim.Lifecycle != lifecycle)
                {
                    await AddConflictAsync(byClaim, actor, now, "lifecycle 与已有台账不一致", ct);
                    results.Add(new VolumeRegistrationResult(
                        claimName, RegistrationStatuses.Conflict, byClaim.Id,
                        "lifecycle 与已有台账不一致"));
                    continue;
                }

                var reactivated =
                    byClaim.State is VolumeStates.Missing or VolumeStates.Unmanaged;
                byClaim.Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax);
                byClaim.MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax);
                byClaim.LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax);
                byClaim.RegisteredUid = pvc.Uid;
                byClaim.State = VolumeStates.Active;
                byClaim.StateReason = null;
                byClaim.MissingSince = null;
                byClaim.LabelState = VolumeLabelStates.Pending;
                byClaim.SetViolations([]);
                byClaim.UpdatedAt = now;
                AddEvent(byClaim,
                    reactivated ? VolumeEventTypes.Reactivated : VolumeEventTypes.Updated,
                    actor, now);
                toPatch.Add(byClaim);
                results.Add(new VolumeRegistrationResult(
                    claimName,
                    reactivated ? RegistrationStatuses.Reactivated : RegistrationStatuses.Updated,
                    byClaim.Id, null));
                continue;
            }

            var volume = new VolumeEntity
            {
                Id = Guid.CreateVersion7(),
                Namespace = namespaceName,
                ClaimName = claimName,
                TenantId = tenantId,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax),
                MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax),
                Lifecycle = lifecycle,
                ContractVersion = VolumeLabels.ContractVersionValue,
                LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax),
                RegisteredUid = pvc.Uid,
                State = VolumeStates.Active,
                LabelState = VolumeLabelStates.Pending,
                RegisteredAt = now,
                UpdatedAt = now,
            };
            db.Volumes.Add(volume);
            rowsByClaim[claimName] = volume;
            tenantRows.Add(volume);
            AddEvent(volume, VolumeEventTypes.Registered, actor, now);
            toPatch.Add(volume);
            results.Add(new VolumeRegistrationResult(
                claimName, RegistrationStatuses.Registered, volume.Id, null));
        }

        await db.SaveChangesAsync(ct);

        foreach (var volume in toPatch.DistinctBy(entity => entity.Id))
        {
            var write = await labels.ApplyAllAsync(volume, ct);
            if (write.Success)
            {
                volume.LabelState = VolumeLabelStates.Applied;
                AddEvent(volume, VolumeEventTypes.LabelApplied, actor, time.GetUtcNow());
                continue;
            }

            for (var index = 0; index < results.Count; index++)
            {
                if (results[index].VolumeId != volume.Id)
                    continue;
                results[index] = results[index] with
                {
                    Status = RegistrationStatuses.LabelPending,
                    Message = write.Error,
                };
            }
            AddEvent(volume, VolumeEventTypes.LabelPatchFailed, actor, time.GetUtcNow(),
                write.Error);
        }

        await db.SaveChangesAsync(ct);
        return new VolumeRegistrationResponse(results);
    }

    private VolumeRegistrationResponse InvalidAll(
        VolumeRegistrationRequest request, string message) => new(
        request.Volumes.Select(item => new VolumeRegistrationResult(
            item.ClaimName?.Trim() ?? "", RegistrationStatuses.Invalid, null, message)).ToArray());

    private Task AddConflictAsync(
        VolumeEntity volume,
        string actor,
        DateTimeOffset now,
        string reason,
        CancellationToken ct)
    {
        AddEvent(volume, VolumeEventTypes.RegistrationConflict, actor, now, reason);
        return Task.CompletedTask;
    }

    private void AddEvent(
        VolumeEntity volume,
        string type,
        string actor,
        DateTimeOffset now,
        string? reason = null,
        object? data = null)
    {
        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = type,
            Actor = actor,
            Reason = reason,
            DataJson = data is null ? "{}" : JsonColumn.Write(data),
            CreatedAt = now,
        });
    }
}
```

**注意：** `AddConflictAsync` 不写库，冲突事件与后续保存一起提交；不要把它改成独立 `SaveChanges`，否则会破坏单次请求的一致性。

- [ ] **Step 4: 实现生命周期服务**

新建 `backend/Infrastructure/Volumes/LifecycleService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class LifecycleService(
    VolumeDbContext db,
    VolumeLabelWriter labels,
    TimeProvider time,
    ILogger<LifecycleService> logger)
{
    public async Task<VolumeLifecycleResponse> ChangeAsync(
        string? tenantId,
        string namespaceName,
        string claimName,
        VolumeLifecycleRequest request,
        bool isAdmin,
        string actor,
        CancellationToken ct)
    {
        var lifecycle = request.Lifecycle?.Trim();
        if (!VolumeLifecycle.IsValid(lifecycle))
            throw new VolumeValidationException("lifecycle 必须为 business 或 ephemeral");

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? null
            : request.Reason.Trim();
        if (reason is { Length: > VolumeValidation.ReasonMax })
            throw new VolumeValidationException("reason 不超过 512 字符");

        var volume = await db.Volumes.SingleOrDefaultAsync(entity =>
            entity.Namespace == namespaceName && entity.ClaimName == claimName, ct)
            ?? throw new VolumeNotFoundException("卷不存在");

        if (!isAdmin && (tenantId is null || volume.TenantId != tenantId))
            throw new VolumeNotFoundException("卷不存在");

        if (volume.Lifecycle == lifecycle)
            return new VolumeLifecycleResponse("unchanged", volume.LabelState);

        if (lifecycle == VolumeLifecycle.Ephemeral && reason is null)
            throw new VolumeValidationException("降级为 ephemeral 必须提供 reason");

        var now = time.GetUtcNow();
        var previous = volume.Lifecycle;
        volume.Lifecycle = lifecycle;
        volume.LabelState = VolumeLabelStates.Pending;
        volume.UpdatedAt = now;

        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = VolumeEventTypes.LifecycleChanged,
            Actor = actor,
            Reason = reason,
            DataJson = JsonColumn.Write(new { from = previous, to = lifecycle }),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        var write = await labels.ApplyLifecycleAsync(volume, lifecycle, ct);
        if (write.Success)
        {
            volume.LabelState = VolumeLabelStates.Applied;
            db.VolumeEvents.Add(new VolumeEventEntity
            {
                Id = Guid.CreateVersion7(),
                VolumeId = volume.Id,
                Namespace = volume.Namespace,
                ClaimName = volume.ClaimName,
                TenantId = volume.TenantId,
                Type = VolumeEventTypes.LabelApplied,
                Actor = actor,
                DataJson = "{}",
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            logger.LogWarning("卷 {Namespace}/{ClaimName} 标签写入失败: {Error}",
                volume.Namespace, volume.ClaimName, write.Error);
            db.VolumeEvents.Add(new VolumeEventEntity
            {
                Id = Guid.CreateVersion7(),
                VolumeId = volume.Id,
                Namespace = volume.Namespace,
                ClaimName = volume.ClaimName,
                TenantId = volume.TenantId,
                Type = VolumeEventTypes.LabelPatchFailed,
                Actor = actor,
                Reason = write.Error,
                DataJson = "{}",
                CreatedAt = time.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct);
        return new VolumeLifecycleResponse(
            write.Success ? "updated" : "label-pending", volume.LabelState);
    }
}
```

- [ ] **Step 5: 实现租户端点**

新建 `backend/Host/Web/VolumeAgentEndpoints.cs`：

```csharp
using System.Security.Claims;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeAgentEndpoints
{
    public static IEndpointRouteBuilder MapVolumeAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/volumes");

        group.MapPost("/register", async (
            VolumeRegistrationRequest request,
            HttpContext context,
            RegistrationService service,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirstValue("tenant");
            if (tenantId is null)
                return Results.Unauthorized();
            return Results.Ok(await service.RegisterAsync(
                tenantId, request, $"tenant:{tenantId}", ct));
        });

        group.MapPost("/{namespaceName}/{claimName}/lifecycle", async (
            string namespaceName,
            string claimName,
            VolumeLifecycleRequest request,
            HttpContext context,
            LifecycleService service,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirstValue("tenant");
            if (tenantId is null)
                return Results.Unauthorized();
            return Results.Ok(await service.ChangeAsync(
                tenantId, namespaceName, claimName, request, false,
                $"tenant:{tenantId}", ct));
        });

        return app;
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~RegistrationServiceTests|FullyQualifiedName~LifecycleServiceTests"`

Expected: 全部通过。

- [ ] **Step 7: 提交**

```bash
git add backend/Infrastructure/Volumes/RegistrationService.cs backend/Infrastructure/Volumes/LifecycleService.cs backend/Host/Web/VolumeAgentEndpoints.cs tests/OpenSandbox.Web.Tests/Volumes/RegistrationServiceTests.cs tests/OpenSandbox.Web.Tests/Volumes/LifecycleServiceTests.cs
git commit -m "feat(volumes): add registration and lifecycle APIs" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 查询、报表、导入与管理端点

**Files:**

- Create: `backend/Infrastructure/Volumes/VolumeQueries.cs`
- Create: `backend/Infrastructure/Volumes/ImportService.cs`
- Create: `backend/Host/Web/VolumeAdminEndpoints.cs`
- Modify: `backend/Host/Web/VolumeAgentEndpoints.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeQueriesTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/ImportServiceTests.cs`

**Interfaces:**

- Consumes: Task 1 契约、Task 2 异常、Task 3 `TenantService`、Task 4 `LifecycleService`
- Produces: `VolumeListQuery`、`VolumeQueries.ListAsync/GetAsync/ReportAsync`
- Produces: `ImportService.ImportAsync(VolumeImportRequest, CancellationToken): Task<VolumeImportResponse>`
- Produces: `/api/v1/volumes*` 管理端点与 `/api/v1/tenant/volumes` 列表端点

- [ ] **Step 1: 写查询失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeQueriesTests.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeQueriesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static VolumeEntity Ledger(
        string claimName = "pvc-1",
        string tenant = "goodcrew",
        string state = VolumeStates.Active,
        string? registeredUid = "uid-1") => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = tenant,
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = VolumeLifecycle.Business,
        RegisteredUid = registeredUid,
        State = state,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now,
        UpdatedAt = Now,
        LastSeenAt = Now,
    };

    private static ClusterVolumeEntity Observation(
        string claimName = "pvc-1",
        string uid = "uid-1",
        string? lifecycle = "business",
        bool mounted = false) => new()
    {
        Namespace = "opensandbox",
        ClaimName = claimName,
        Uid = uid,
        Phase = "Bound",
        StorageClass = "standard",
        Capacity = "1Gi",
        CreatedAtK8s = Now,
        Mounted = mounted,
        MountedByJson = mounted ? "[\"pod-1\"]" : "[]",
        LabelsJson = lifecycle is null
            ? "{}"
            : $"{{\"opensandbox.io/volume-lifecycle\":\"{lifecycle}\"}}",
        LifecycleLabel = lifecycle,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        LastScanId = Guid.CreateVersion7(),
    };

    [Fact]
    public async Task List_MergesLedgerAndObservations()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger());
        test.Db.ClusterVolumes.AddRange(
            Observation("pvc-1"),
            Observation("pvc-2", uid: "uid-2", lifecycle: "ephemeral"),
            Observation("pvc-3", uid: "uid-3", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 1, PageSize = 20 }, null, CancellationToken.None);

        Assert.Equal(3, response.Items.Count);
        Assert.Equal(3, response.Pagination.TotalItems);
        Assert.Equal(VolumeClassifications.Registered,
            response.Items.Single(item => item.ClaimName == "pvc-1").Classification);
        Assert.Equal(VolumeClassifications.Unregistered,
            response.Items.Single(item => item.ClaimName == "pvc-2").Classification);
        Assert.Equal(VolumeClassifications.Unlabeled,
            response.Items.Single(item => item.ClaimName == "pvc-3").Classification);
        Assert.Equal("ephemeral",
            response.Items.Single(item => item.ClaimName == "pvc-2").Lifecycle);
    }

    [Fact]
    public async Task List_FilterUsesTotalBeforePaging()
    {
        using var test = new TestDb();
        for (var index = 0; index < 25; index++)
            test.Db.ClusterVolumes.Add(Observation($"pvc-{index:00}", uid: $"uid-{index}"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 3, PageSize = 10 }, null, CancellationToken.None);

        Assert.Equal(25, response.Pagination.TotalItems);
        Assert.Equal(3, response.Pagination.TotalPages);
        Assert.Equal(5, response.Items.Count);
        Assert.False(response.Pagination.HasNextPage);
    }

    [Fact]
    public async Task List_OutOfRangePage_IsEmptyButTotalRemains()
    {
        using var test = new TestDb();
        test.Db.ClusterVolumes.Add(Observation());
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery { Page = 5, PageSize = 10 }, null, CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Pagination.TotalItems);
    }

    [Fact]
    public async Task List_ClassificationAndMountedFilters()
    {
        using var test = new TestDb();
        test.Db.ClusterVolumes.AddRange(
            Observation("pvc-1", mounted: true),
            Observation("pvc-2", uid: "uid-2", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var mounted = await queries.ListAsync(
            new VolumeListQuery { Classification = "unregistered", Mounted = true },
            null, CancellationToken.None);

        Assert.Single(mounted.Items);
        Assert.Equal("pvc-1", mounted.Items[0].ClaimName);
    }

    [Fact]
    public async Task List_ForcedTenant_OnlyShowsTenantLedger()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger());
        test.Db.Volumes.Add(Ledger("pvc-2", tenant: "other"));
        test.Db.ClusterVolumes.Add(Observation("pvc-3", uid: "uid-3"));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var response = await queries.ListAsync(
            new VolumeListQuery(), "goodcrew", CancellationToken.None);

        Assert.Single(response.Items);
        Assert.Equal("pvc-1", response.Items[0].ClaimName);
    }

    [Fact]
    public async Task Get_ObservedButUnregistered_IsVisibleToAdminOnly()
    {
        using var test = new TestDb();
        test.Db.ClusterVolumes.Add(Observation("pvc-9", uid: "uid-9", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var admin = await queries.GetAsync(
            "opensandbox", "pvc-9", null, CancellationToken.None);
        var tenant = await queries.GetAsync(
            "opensandbox", "pvc-9", "goodcrew", CancellationToken.None);

        Assert.NotNull(admin);
        Assert.Equal(VolumeClassifications.Unlabeled, admin!.Volume.Classification);
        Assert.Null(tenant);
    }

    [Fact]
    public async Task Report_CountsAndTruncatesAt200()
    {
        using var test = new TestDb();
        for (var index = 0; index < 201; index++)
            test.Db.ClusterVolumes.Add(Observation($"pvc-{index:000}", uid: $"uid-{index}", lifecycle: null));
        await test.Db.SaveChangesAsync();
        var queries = new VolumeQueries(test.Db);

        var report = await queries.ReportAsync(CancellationToken.None);

        Assert.Equal(201, report.Counts.Observed);
        Assert.Equal(201, report.Counts.Unlabeled);
        Assert.True(report.Truncated);
        Assert.Equal(200, report.Violations.Count);
        Assert.Equal(0m, report.Coverage.LabeledPercent);
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/ImportServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ImportServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static async Task<(TestDb Test, FakeVolumeKubernetesClient Kube)> SeedAsync()
    {
        var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", uid: "uid-1");
        return (test, kube);
    }

    private static ImportService Service(TestDb test, FakeVolumeKubernetesClient kube) =>
        new(test.Db, kube, new VolumeLabelWriter(kube), new FixedTimeProvider(Now));

    private static VolumeImportRequest Request(bool? dryRun = null) => new(
        dryRun,
        [new VolumeImportItem(
            "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
            "meta", "/workspace", "business", "sb-1")]);

    [Fact]
    public async Task Import_DefaultsToDryRunWithoutWrites()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.True(response.DryRun);
        Assert.Equal("would-register", response.Results[0].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
        Assert.Empty(kube.Patches);
    }

    [Fact]
    public async Task Import_WriteMode_CreatesLedgerAndPatchesLabels()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;

        var response = await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        Assert.False(response.DryRun);
        Assert.Equal(RegistrationStatuses.Registered, response.Results[0].Status);
        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeLabelStates.Applied, volume.LabelState);
        Assert.Single(kube.Patches);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(
            e => e.Type == VolumeEventTypes.Imported));
    }

    [Fact]
    public async Task Import_DryRunExistingOwner_ReturnsWouldUpdate()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        await Service(test, kube).ImportAsync(Request(dryRun: false), CancellationToken.None);

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal("would-update", response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_DryRunSameOwnerDifferentClaims_SecondIsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2");

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(true,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-1", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-1"),
                new VolumeImportItem(
                    "goodcrew", "opensandbox", "pvc-2", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-2"),
            ]),
            CancellationToken.None);

        Assert.Equal("would-register", response.Results[0].Status);
        Assert.Equal(RegistrationStatuses.Conflict, response.Results[1].Status);
        Assert.Equal(0, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_SameOwnerInAnotherNamespace_ReturnsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity
        {
            Namespace = "opensandbox-2",
            TenantId = "goodcrew",
        });
        await test.Db.SaveChangesAsync();
        kube.Namespaces["opensandbox-2"] = new NamespaceInfo(
            "opensandbox-2",
            new Dictionary<string, string> { [VolumeLabels.Managed] = "true" });
        kube.AddPvc("opensandbox-2", "pvc-2", uid: "uid-2");
        await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        var response = await Service(test, kube).ImportAsync(
            new VolumeImportRequest(false,
            [
                new VolumeImportItem(
                    "goodcrew", "opensandbox-2", "pvc-2", "employee", "3f2a",
                    "meta", "/workspace", "business", "sb-2"),
            ]),
            CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }

    [Fact]
    public async Task Import_OtherTenantOwnsClaim_ReturnsConflict()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.Volumes.Add(new VolumeEntity
        {
            Id = Guid.CreateVersion7(), Namespace = "opensandbox", ClaimName = "pvc-1",
            TenantId = "other", OwnerKind = "employee", OwnerId = "other",
            Lifecycle = VolumeLifecycle.Business, RegisteredAt = Now, UpdatedAt = Now,
            RegisteredUid = "uid-1", State = VolumeStates.Active,
            LabelState = VolumeLabelStates.Applied,
        });
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(
            Request(dryRun: false), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Conflict, response.Results[0].Status);
        Assert.Equal("other", (await test.Db.Volumes.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Import_UnassignedNamespace_ReturnsInvalid()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        test.Db.TenantNamespaces.RemoveRange(test.Db.TenantNamespaces);
        await test.Db.SaveChangesAsync();

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.Invalid, response.Results[0].Status);
    }

    [Fact]
    public async Task Import_MissingPvc_ReturnsNotFound()
    {
        var (test, kube) = await SeedAsync();
        using var _ = test;
        kube.Pvcs["opensandbox"].Clear();

        var response = await Service(test, kube).ImportAsync(Request(), CancellationToken.None);

        Assert.Equal(RegistrationStatuses.NotFound, response.Results[0].Status);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeQueriesTests|FullyQualifiedName~ImportServiceTests"`

Expected: 编译失败，提示 `VolumeQueries` / `ImportService` 不存在。

- [ ] **Step 3: 实现查询与报表**

新建 `backend/Infrastructure/Volumes/VolumeQueries.cs`：

```csharp
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
            filtered = filtered.Where(item => item.Tenant == forcedTenant);
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
        var items = stable
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

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

        var events = await db.VolumeEvents.AsNoTracking()
            .Where(entry => entry.Namespace == namespaceName && entry.ClaimName == claimName)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

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
            .GroupBy(item => item.Tenant ?? "<unlabeled>")
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
        foreach (var volume in observations.Where(volume =>
                     volume.TenantLabel is not null &&
                     !knownTenants.Contains(volume.TenantLabel)))
        {
            violations.Add(new VolumeViolationDto(
                "unknown-tenant", volume.Namespace, volume.ClaimName,
                $"未知租户标签: {volume.TenantLabel}"));
        }

        var truncated = violations.Count > MaxViolations;
        var violationPage = violations.Take(MaxViolations).ToArray();
        var lastScan = await db.ScanRuns.AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(ct);

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
```

- [ ] **Step 4: 实现导入服务**

新建 `backend/Infrastructure/Volumes/ImportService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class ImportService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    TimeProvider time)
{
    public async Task<VolumeImportResponse> ImportAsync(
        VolumeImportRequest request,
        CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0)
            throw new VolumeValidationException("items 不能为空");

        var dryRun = request.DryRun ?? true;
        var items = request.Items;
        var tenantIds = items.Select(item => item.Tenant?.Trim() ?? "")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var tenants = await db.Tenants.AsNoTracking()
            .Include(tenant => tenant.Namespaces)
            .Where(tenant => tenantIds.Contains(tenant.Id))
            .ToListAsync(ct);
        var tenantIndex = tenants.ToDictionary(tenant => tenant.Id, StringComparer.Ordinal);

        var managedCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pvcCache = new Dictionary<string, Dictionary<string, ObservedPvc>>(StringComparer.Ordinal);

        foreach (var namespaceName in items
                     .Select(item => item.Namespace?.Trim() ?? "")
                     .Where(name => VolumeValidation.Namespace(name) is not null)
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                var info = await kube.GetNamespaceAsync(namespaceName, ct);
                managedCache[namespaceName] =
                    info is not null &&
                    info.Labels.TryGetValue(VolumeLabels.Managed, out var managed) &&
                    managed == "true";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new VolumeDependencyException($"Kubernetes 暂不可用: {exception.Message}");
            }
        }

        foreach (var namespaceName in managedCache
                     .Where(pair => pair.Value)
                     .Select(pair => pair.Key))
        {
            try
            {
                pvcCache[namespaceName] = (await kube.ListPvcsAsync(namespaceName, ct))
                    .ToDictionary(pvc => pvc.ClaimName, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new VolumeDependencyException($"Kubernetes 暂不可用: {exception.Message}");
            }
        }

        var namespaceNames = items
            .Select(item => item.Namespace?.Trim() ?? "")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // 命名空间内用于 claim 冲突；租户全量用于跨命名空间 owner 冲突。
        var existing = await db.Volumes
            .Where(volume =>
                namespaceNames.Contains(volume.Namespace) ||
                tenantIds.Contains(volume.TenantId))
            .ToListAsync(ct);

        var results = new List<VolumeImportResult>();
        var pendingPatches = new List<VolumeEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = time.GetUtcNow();

        foreach (var item in items)
        {
            var namespaceName = item.Namespace?.Trim() ?? "";
            var claimName = item.ClaimName?.Trim() ?? "";
            var key = $"{namespaceName}/{claimName}";

            var validation = VolumeValidation.ValidateImportItem(item);
            if (validation is not null)
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, validation));
                continue;
            }

            if (!seen.Add(key))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "重复的导入项"));
                continue;
            }

            if (!tenantIndex.TryGetValue(item.Tenant.Trim(), out var tenant) ||
                tenant.Status != "active")
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "租户不存在或已停用"));
                continue;
            }

            if (!tenant.Namespaces.Any(mapping => mapping.Namespace == namespaceName))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "命名空间未分配给该租户"));
                continue;
            }

            if (!managedCache.GetValueOrDefault(namespaceName))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Invalid, "命名空间未标记为受管"));
                continue;
            }

            var pvcs = pvcCache[namespaceName];
            if (!pvcs.TryGetValue(claimName, out var pvc))
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.NotFound, "PVC 不存在"));
                continue;
            }

            var ownerKind = item.OwnerKind.Trim();
            var ownerId = item.OwnerId.Trim();
            var lifecycle = item.Lifecycle.Trim();
            var byClaim = existing.FirstOrDefault(volume =>
                volume.Namespace == namespaceName && volume.ClaimName == claimName);

            if (byClaim is not null)
            {
                if (byClaim.TenantId != tenant.Id ||
                    byClaim.OwnerKind != ownerKind ||
                    byClaim.OwnerId != ownerId ||
                    byClaim.Lifecycle != lifecycle)
                {
                    results.Add(new VolumeImportResult(
                        namespaceName, claimName, RegistrationStatuses.Conflict, "与已有台账冲突"));
                    continue;
                }

                if (dryRun)
                {
                    results.Add(new VolumeImportResult(
                        namespaceName, claimName, "would-update", null));
                    continue;
                }

                byClaim.Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax);
                byClaim.MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax);
                byClaim.LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax);
                byClaim.LabelState = VolumeLabelStates.Pending;
                byClaim.UpdatedAt = now;
                AddEvent(byClaim, VolumeEventTypes.Imported, now);
                pendingPatches.Add(byClaim);
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Updated, null));
                continue;
            }

            var ownerRow = existing.FirstOrDefault(volume =>
                volume.TenantId == tenant.Id &&
                volume.OwnerKind == ownerKind &&
                volume.OwnerId == ownerId &&
                (volume.Namespace != namespaceName || volume.ClaimName != claimName));
            if (ownerRow is not null)
            {
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, RegistrationStatuses.Conflict,
                    "owner 已绑定其他 PVC"));
                continue;
            }

            var volume = new VolumeEntity
            {
                Id = Guid.CreateVersion7(),
                Namespace = namespaceName,
                ClaimName = claimName,
                TenantId = tenant.Id,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                Role = VolumeValidation.Text(item.Role, VolumeValidation.RoleMax),
                MountPath = VolumeValidation.Text(item.MountPath, VolumeValidation.MountPathMax),
                Lifecycle = lifecycle,
                ContractVersion = VolumeLabels.ContractVersionValue,
                LastSandboxId = VolumeValidation.Text(item.SandboxId, VolumeValidation.SandboxIdMax),
                RegisteredUid = pvc.Uid,
                State = VolumeStates.Active,
                LabelState = VolumeLabelStates.Pending,
                RegisteredAt = now,
                UpdatedAt = now,
            };

            if (dryRun)
            {
                // 预演同样占用 owner/claim，让同批次后续项得到与写入模式一致的冲突结果。
                existing.Add(volume);
                results.Add(new VolumeImportResult(
                    namespaceName, claimName, "would-register", null));
                continue;
            }

            db.Volumes.Add(volume);
            existing.Add(volume);
            AddEvent(volume, VolumeEventTypes.Imported, now);
            pendingPatches.Add(volume);
            results.Add(new VolumeImportResult(
                namespaceName, claimName, RegistrationStatuses.Registered, null));
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);

            foreach (var volume in pendingPatches.DistinctBy(entity => entity.Id))
            {
                var write = await labels.ApplyAllAsync(volume, ct);
                if (write.Success)
                {
                    volume.LabelState = VolumeLabelStates.Applied;
                    AddEvent(volume, VolumeEventTypes.LabelApplied, time.GetUtcNow());
                    continue;
                }

                for (var index = 0; index < results.Count; index++)
                {
                    if (results[index].Namespace != volume.Namespace ||
                        results[index].ClaimName != volume.ClaimName)
                        continue;
                    results[index] = results[index] with
                    {
                        Status = RegistrationStatuses.LabelPending,
                        Message = write.Error,
                    };
                }
                AddEvent(volume, VolumeEventTypes.LabelPatchFailed, time.GetUtcNow(),
                    write.Error);
            }

            await db.SaveChangesAsync(ct);
        }

        return new VolumeImportResponse(dryRun, results);
    }

    private void AddEvent(VolumeEntity volume, string type, DateTimeOffset now, string? reason = null)
    {
        db.VolumeEvents.Add(new VolumeEventEntity
        {
            Id = Guid.CreateVersion7(),
            VolumeId = volume.Id,
            Namespace = volume.Namespace,
            ClaimName = volume.ClaimName,
            TenantId = volume.TenantId,
            Type = type,
            Actor = "admin",
            Reason = reason,
            DataJson = "{}",
            CreatedAt = now,
        });
    }
}
```

- [ ] **Step 5: 实现管理端点与租户列表**

新建 `backend/Host/Web/VolumeAdminEndpoints.cs`：

```csharp
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeAdminEndpoints
{
    public static IEndpointRouteBuilder MapVolumeAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/volumes");

        group.MapGet("", async (
            [AsParameters] VolumeListQuery query,
            VolumeQueries queries,
            CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(query, null, ct)));

        group.MapGet("/report", async (
            VolumeQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ReportAsync(ct)));

        group.MapPost("/scan", async (
            ScanCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(ToResponse(await coordinator.ScanAsync("manual", ct))));

        group.MapPost("/import", async (
            VolumeImportRequest request, ImportService service, CancellationToken ct) =>
            Results.Ok(await service.ImportAsync(request, ct)));

        group.MapGet("/{namespaceName}/{claimName}", async (
            string namespaceName,
            string claimName,
            VolumeQueries queries,
            CancellationToken ct) =>
        {
            var detail = await queries.GetAsync(namespaceName, claimName, null, ct);
            return detail is null
                ? Results.NotFound(new ErrorResponse("not_found", "卷不存在"))
                : Results.Ok(detail);
        });

        group.MapPost("/{namespaceName}/{claimName}/lifecycle", async (
            string namespaceName,
            string claimName,
            VolumeLifecycleRequest request,
            LifecycleService service,
            CancellationToken ct) =>
            Results.Ok(await service.ChangeAsync(
                null, namespaceName, claimName, request, true, "admin", ct)));

        return app;
    }

    private static VolumeScanResponse ToResponse(ScanSummary summary) => new(
        summary.ScanId,
        summary.Trigger,
        summary.StartedAt,
        summary.FinishedAt,
        summary.NamespacesScanned,
        summary.NamespacesSucceeded,
        summary.VolumeCount,
        summary.Errors.Select(error => new ScanErrorDto(error.Namespace, error.Message)).ToArray());
}
```

修改 `backend/Host/Web/VolumeAgentEndpoints.cs`，在 `MapPost("/register", ...)` 之后加：

```csharp
        group.MapGet("", async (
            [AsParameters] VolumeListQuery query,
            HttpContext context,
            VolumeQueries queries,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirstValue("tenant");
            if (tenantId is null)
                return Results.Unauthorized();
            query.Tenant = tenantId;
            return Results.Ok(await queries.ListAsync(query, tenantId, ct));
        });
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeQueriesTests|FullyQualifiedName~ImportServiceTests"`

Expected: 全部通过。

- [ ] **Step 7: 提交**

```bash
git add backend/Infrastructure/Volumes/VolumeQueries.cs backend/Infrastructure/Volumes/ImportService.cs backend/Host/Web/VolumeAdminEndpoints.cs backend/Host/Web/VolumeAgentEndpoints.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeQueriesTests.cs tests/OpenSandbox.Web.Tests/Volumes/ImportServiceTests.cs
git commit -m "feat(volumes): add queries, report, import and admin endpoints" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: 依赖注入、健康检查与集成验收

**Files:**

- Create: `backend/Infrastructure/DependencyInjection/VolumeManagerRegistration.cs`
- Create: `backend/Host/Web/OpsEndpoints.cs`
- Modify: `backend/Program.cs`
- Modify: `backend/appsettings.json`
- Modify: `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/OpsEndpointsTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Endpoints/VolumeEndpointIntegrationTests.cs`

**Interfaces:**

- Consumes: Task 1–5 全部类型；Plan A 的 `AddConsoleAuth`、`UseConsoleRbac`、`TestAuthHandler`
- Produces: `AddVolumeManager(IServiceCollection, IConfiguration)`、`MigrateVolumeManagerAsync(IServiceProvider, CancellationToken)`
- Produces: `ApiFactory.VolumeKube`、`ApiFactory.NewVolumeDb()`、`ApiFactory.CreateTenantClient(string)`
- Produces: `/healthz`、`/readyz`

- [ ] **Step 1: 写集成失败测试**

新建 `tests/OpenSandbox.Web.Tests/Endpoints/OpsEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class OpsEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public OpsEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task Readyz_IsAnonymous()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ready", body!.Status);
    }

    private sealed record HealthBody(string Status);
}
```

新建 `tests/OpenSandbox.Web.Tests/Endpoints/VolumeEndpointIntegrationTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class VolumeEndpointIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VolumeEndpointIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task VolumeList_RequiresVolumeRole()
    {
        var anonymous = await _factory.CreateAnonymousClient().GetAsync("/api/v1/volumes");
        var sandboxOnly = await _factory.CreateClientWithRoles("sandbox:admin")
            .GetAsync("/api/v1/volumes");
        var viewer = await _factory.CreateClientWithRoles("volume:viewer")
            .GetAsync("/api/v1/volumes");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sandboxOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
    }

    [Fact]
    public async Task TenantKey_CanRegister_AndAdminSeesLedger()
    {
        var admin = _factory.CreateClientWithRoles("volume:admin");
        var createTenant = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants",
            new CreateTenantRequest("goodcrew", "GoodCrew", ["opensandbox"]));
        createTenant.EnsureSuccessStatusCode();

        var createKey = await admin.PostAsJsonAsync(
            "/api/v1/volume-tenants/goodcrew/keys",
            new CreateTenantKeyRequest("integration"));
        createKey.EnsureSuccessStatusCode();
        var key = await createKey.Content.ReadFromJsonAsync<CreateTenantKeyResponse>();

        _factory.VolumeKube.AddPvc("opensandbox", "pvc-1");
        var tenant = _factory.CreateTenantClient(key!.Key);
        var register = await tenant.PostAsJsonAsync(
            "/api/v1/tenant/volumes/register",
            new VolumeRegistrationRequest("opensandbox",
            [
                new VolumeRegistrationItem(
                    "pvc-1", "employee", "3f2a", "meta", "/workspace", "business", "sb-1"),
            ]));
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<VolumeRegistrationResponse>();

        Assert.Equal(RegistrationStatuses.Registered, registered!.Results[0].Status);
        Assert.Single(_factory.VolumeKube.Patches);

        var list = await admin.GetFromJsonAsync<VolumeListResponse>("/api/v1/volumes");
        var item = Assert.Single(list!.Items, volume => volume.ClaimName == "pvc-1");
        Assert.Equal("goodcrew", item.Tenant);
        Assert.Equal(VolumeClassifications.Registered, item.Classification);
    }

    [Fact]
    public async Task TenantEndpoint_WithoutKey_Returns401()
    {
        var response = await _factory.CreateAnonymousClient().PostAsJsonAsync(
            "/api/v1/tenant/volumes/register",
            new VolumeRegistrationRequest("opensandbox", []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~OpsEndpointsTests|FullyQualifiedName~VolumeEndpointIntegrationTests"`

Expected: 编译失败，提示 `/healthz` 返回 404 或 `VolumeKube` 不存在。

- [ ] **Step 3: 实现依赖注入注册与迁移**

新建 `backend/Infrastructure/DependencyInjection/VolumeManagerRegistration.cs`：

```csharp
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.DependencyInjection;

public static class VolumeManagerRegistration
{
    public static IServiceCollection AddVolumeManager(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(VolumeManagerOptions.SectionName);
        var options = section.Get<VolumeManagerOptions>() ?? new VolumeManagerOptions();
        services.Configure<VolumeManagerOptions>(section);
        services.AddSingleton(options);

        var connectionString = configuration.GetConnectionString("VolumeManager");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (options.Enabled)
                throw new InvalidOperationException("ConnectionStrings:VolumeManager 未配置");
            connectionString =
                "Host=localhost;Database=opensandbox;Username=opensandbox;Password=opensandbox";
        }

        services.AddDbContext<VolumeDbContext>(builder => builder.UseNpgsql(connectionString));
        services.AddSingleton<IKubernetes>(_ => BuildKubernetes(options));
        services.AddSingleton<IVolumeKubernetesClient, KubernetesVolumeClient>();
        services.AddSingleton<VolumeLabelWriter>();
        services.AddSingleton<ScanCoordinator>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<InventoryScanService>();
        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<TenantService>();
        services.AddScoped<VolumeQueries>();
        services.AddScoped<RegistrationService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<ImportService>();
        services.AddHostedService<InventoryScanner>();
        return services;
    }

    public static async Task MigrateVolumeManagerAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<VolumeManagerOptions>>().Value;
        if (!options.Enabled || !options.MigrateOnStartup)
            return;

        var db = scope.ServiceProvider.GetRequiredService<VolumeDbContext>();
        await db.Database.MigrateAsync(ct);
    }

    private static IKubernetes BuildKubernetes(VolumeManagerOptions options)
    {
        var configuration = string.IsNullOrWhiteSpace(options.KubeConfigPath)
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(options.KubeConfigPath);
        return new Kubernetes(configuration);
    }
}
```

- [ ] **Step 4: 实现健康检查端点**

新建 `backend/Host/Web/OpsEndpoints.cs`：

```csharp
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Host.Web;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous();

        app.MapGet("/readyz", async (
            VolumeDbContext db,
            IOptions<VolumeManagerOptions> options,
            CancellationToken ct) =>
        {
            if (!options.Value.Enabled)
                return Results.Ok(new { status = "ready", checks = new { database = "disabled" } });

            try
            {
                if (await db.Database.CanConnectAsync(ct))
                    return Results.Ok(new { status = "ready", checks = new { database = "ok" } });
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }

            return Results.Json(
                new { status = "not-ready", checks = new { database = "fail" } },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        return app;
    }
}
```

- [ ] **Step 5: 配置 appsettings 并接入 Program**

修改 `backend/appsettings.json`，在根对象追加：

```json
  "ConnectionStrings": {
    "VolumeManager": ""
  },
  "VolumeManager": {
    "Enabled": false,
    "ScanInterval": "00:05:00",
    "ScanOnStartup": true,
    "ManagedNamespaceSelector": "opensandbox.io/volume-managed=true",
    "MaxConcurrentNamespaces": 4,
    "KubeConfigPath": null,
    "MigrateOnStartup": true
  }
```

完整文件应为（保留原有 `OpenSandbox` 节点）：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "VolumeManager": ""
  },
  "OpenSandbox": {
    "Domain": "",
    "ApiKey": "",
    "Protocol": "https",
    "RequestTimeoutSeconds": 30,
    "DisableMetrics": false
  },
  "VolumeManager": {
    "Enabled": false,
    "ScanInterval": "00:05:00",
    "ScanOnStartup": true,
    "ManagedNamespaceSelector": "opensandbox.io/volume-managed=true",
    "MaxConcurrentNamespaces": 4,
    "KubeConfigPath": null,
    "MigrateOnStartup": true
  }
}
```

把 `backend/Program.cs` 整体替换为：

```csharp
using System.Text.Json;
using OpenSandbox.Web.Host.Security;
using OpenSandbox.Web.Host.Web;
using OpenSandbox.Web.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddOpenSandbox(builder.Configuration);
builder.Services.AddConsoleAuth(builder.Configuration);
builder.Services.AddVolumeManager(builder.Configuration);

var app = builder.Build();

await app.MigrateVolumeManagerAsync();

app.UseMiddleware<SandboxExceptionMiddleware>();

// 静态资源与 SPA fallback 必须匿名；index.html 在这里注入登录配置。
app.MapSpaFallback();

// 卷异常中间件必须在 SandboxExceptionMiddleware 之内，否则会被它先翻译成 500。
app.UseMiddleware<VolumeExceptionMiddleware>();
// 租户 Key 必须在 UseAuthentication 之前写入 Principal；TestAuthHandler 无头时返回 NoResult，不会覆盖。
app.UseMiddleware<TenantApiKeyMiddleware>();

app.UseAuthentication();
app.UseConsoleRbac();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapOpsEndpoints();
app.MapSandboxEndpoints();
app.MapMetricsEndpoints();
app.MapConsoleEndpoints();
app.MapVolumeTenantEndpoints();
app.MapVolumeAgentEndpoints();
app.MapVolumeAdminEndpoints();

app.Run();

public partial class Program;
```

- [ ] **Step 6: 扩展测试工厂**

把 `tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs` 整体替换为（保留 Plan A 的鉴权设置）：

```csharp
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSandbox.Web.Infrastructure.Gateways;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeSandboxGateway Gateway { get; } = new();
    public FakeVolumeKubernetesClient VolumeKube { get; } =
        FakeVolumeKubernetesClient.WithManagedNamespace();

    public ApiFactory()
    {
        _connection.Open();
        using var db = NewVolumeDb();
        db.Database.EnsureCreated();
    }

    public VolumeDbContext NewVolumeDb()
    {
        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new VolumeDbContext(options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:VolumeManager", "Host=unused;Database=unused");
        builder.UseSetting("VolumeManager:Enabled", "false");
        builder.UseSetting("VolumeManager:MigrateOnStartup", "false");
        builder.UseSetting("VolumeManager:ScanOnStartup", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISandboxGateway>();
            services.AddSingleton<ISandboxGateway>(Gateway);

            services.RemoveAll<DbContextOptions<VolumeDbContext>>();
            services.RemoveAll<VolumeDbContext>();
            // EF Core 9+ 把 optionsAction 注册为 IDbContextOptionsConfiguration，只删 options 会残留 Npgsql 配置。
            services.RemoveAll<IDbContextOptionsConfiguration<VolumeDbContext>>();
            services.AddDbContext<VolumeDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IVolumeKubernetesClient>();
            services.AddSingleton<IVolumeKubernetesClient>(VolumeKube);

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }

    public new HttpClient CreateClient() =>
        CreateClientWithRoles("sandbox:admin", "volume:admin");

    public HttpClient CreateClientWithRoles(params string[] roles)
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "test-admin");
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    public HttpClient CreateAnonymousClient() => base.CreateClient();

    public HttpClient CreateTenantClient(string apiKey)
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
```

- [ ] **Step 7: 运行集成测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~OpsEndpointsTests|FullyQualifiedName~VolumeEndpointIntegrationTests"`

Expected: 全部通过。

- [ ] **Step 8: 运行全部测试**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过；Plan A 的鉴权测试与原有沙箱测试不受影响。

- [ ] **Step 9: 手工冒烟（可选，需要真实 Postgres 与集群）**

```bash
docker run --rm -d --name vm-pg -e POSTGRES_PASSWORD=opensandbox -e POSTGRES_USER=opensandbox -e POSTGRES_DB=opensandbox -p 5432:5432 postgres:17
cd backend
ConnectionStrings__VolumeManager="Host=localhost;Database=opensandbox;Username=opensandbox;Password=opensandbox" VolumeManager__Enabled=true dotnet run
```

另一个终端：

```bash
curl -s http://localhost:5080/healthz
curl -s http://localhost:5080/readyz
```

Expected：`{"status":"ok"}` 与 `{"status":"ready","checks":{"database":"ok"}}`。完成后停止后端并 `docker rm -f vm-pg`。

- [ ] **Step 10: 提交**

```bash
git add backend/Infrastructure/DependencyInjection/VolumeManagerRegistration.cs backend/Host/Web/OpsEndpoints.cs backend/Program.cs backend/appsettings.json tests/OpenSandbox.Web.Tests/Endpoints/ApiFactory.cs tests/OpenSandbox.Web.Tests/Endpoints/OpsEndpointsTests.cs tests/OpenSandbox.Web.Tests/Endpoints/VolumeEndpointIntegrationTests.cs
git commit -m "feat(volumes): wire volume manager and add readiness checks" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---
