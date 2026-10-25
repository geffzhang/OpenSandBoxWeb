# Volume Manager Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立卷治理的纯领域层、PostgreSQL 持久化与周期盘点器：标签分类、对账状态机、EF Core 模型、Kubernetes 扫描与标签重试。

**Architecture:** 先写零依赖的纯函数（分类器、对账器），再用 EF Core 映射台账/观测/审计三组表，最后用 `IVolumeKubernetesClient` 抽象隔离 K8s API，并实现单轮扫描：发现命名空间 → 有界并发拉取 PVC/Pod → 落库观测 → 纯函数对账 → 标签重试 → 批量消失检测。

**Tech Stack:** .NET 10、EF Core 10 + Npgsql、KubernetesClient、xUnit 2.9.3、SQLite（仅测试关系语义）。

**Spec:** `docs/superpowers/specs/2026-10-25-global-volume-manager-design.md`（§4.1 FR-2/FR-5/FR-6、§6.1–§6.3、§6.7）

## Global Constraints

- 组件永不删除 PVC/PV；`IVolumeKubernetesClient` 不提供删除方法，`ClusterRole` 也不会有 `delete`。
- 分类：缺失/空白 → `Unlabeled`；`ephemeral` → `Ephemeral`；`business` → `Business`；其他任何值 → `Business`（fail-closed）。比较前 `Trim()`，但大小写敏感。
- 对账只依据台账 lifecycle，不依据标签当前值；标签差异只算漂移。
- 命名空间 list 失败时**不做任何状态迁移**，不删除观测，不标 `missing`。
- 只有「本轮 list 成功且清单中确实没有该 PVC」才允许 `active → missing`。
- 命名空间 404 → 台账 `missing(namespace-deleted)` + 一条批量事件，不逐条写 `unexpected-disappearance`。
- `volume_events` append-only；台账行永不物理删除。
- owner 标签规范化后超长或首尾非字母数字 → 省略标签，台账保留原始值。
- 扫描并发只用于 K8s 拉取；`DbContext` 不得跨线程并发使用。
- 测试使用 SQLite 内存库；生产迁移使用 Npgsql。JSON 列在 Npgsql 下为 `jsonb`，测试下为 `TEXT`。
- 本计划不接线 `Program.cs`，不提供 HTTP 端点；这些在 `2026-10-25-volume-manager-api.md`。
- 用户可见文案与日志沿用现有中文风格。

---

### Task 1: 标签契约与分类器

**Files:**

- Create: `backend/Infrastructure/Volumes/VolumeLabels.cs`
- Create: `backend/Infrastructure/Volumes/VolumeValues.cs`
- Create: `backend/Infrastructure/Volumes/VolumeClassifier.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelsTests.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeClassifierTests.cs`

**Interfaces:**

- Produces: `VolumeLabels.Managed|Lifecycle|Tenant|Owner|ContractVersion|ContractVersionValue`
- Produces: `VolumeLabels.NormalizeOwner(string ownerKind, string ownerId): string?`
- Produces: `VolumeLifecycle.Business|Ephemeral`、`VolumeLifecycle.IsValid(string?)`
- Produces: `VolumeLabelStates.Applied|Pending`
- Produces: `VolumeStates.Active|Missing|Unmanaged|EphemeralGone|Unexpected|NamespaceDeleted|NeverObserved`
- Produces: `VolumeEventTypes` 的 18 个事件常量
- Produces: `VolumeKind { Unlabeled, Ephemeral, Business }`、`VolumeClassifier.Classify(string?)`、`VolumeClassifier.ToApiValue(VolumeKind)`

- [ ] **Step 1: 写标签与分类失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelsTests.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeLabelsTests
{
    [Fact]
    public void NormalizeOwner_JoinsKindAndId()
        => Assert.Equal("employee-3f2a9c1b7d4e", VolumeLabels.NormalizeOwner("employee", "3f2a9c1b7d4e"));

    [Fact]
    public void NormalizeOwner_ReplacesInvalidCharacters()
        => Assert.Equal("employee-3f2a-9c", VolumeLabels.NormalizeOwner("employee", "3f2a:9c"));

    [Fact]
    public void NormalizeOwner_KeepsDotsUnderscoresAndDashes()
        => Assert.Equal("employee-a.b_c-d", VolumeLabels.NormalizeOwner("employee", "a.b_c-d"));

    [Fact]
    public void NormalizeOwner_OmitsWhenLongerThan63()
        => Assert.Null(VolumeLabels.NormalizeOwner("employee", new string('a', 56)));

    [Theory]
    [InlineData("", "abc")]
    [InlineData("employee", "")]
    [InlineData("employee", "!!!")]
    public void NormalizeOwner_OmitsInvalidResults(string kind, string id)
        => Assert.Null(VolumeLabels.NormalizeOwner(kind, id));

    [Fact]
    public void ContractVersion_IsOne()
        => Assert.Equal("1", VolumeLabels.ContractVersionValue);

    [Fact]
    public void KeyNames_AreExact()
    {
        Assert.Equal("opensandbox.io/volume-managed", VolumeLabels.Managed);
        Assert.Equal("opensandbox.io/volume-lifecycle", VolumeLabels.Lifecycle);
        Assert.Equal("opensandbox.io/volume-tenant", VolumeLabels.Tenant);
        Assert.Equal("opensandbox.io/volume-owner", VolumeLabels.Owner);
        Assert.Equal("opensandbox.io/volume-contract-version", VolumeLabels.ContractVersion);
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeClassifierTests.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeClassifierTests
{
    [Theory]
    [InlineData(null, VolumeKind.Unlabeled)]
    [InlineData("", VolumeKind.Unlabeled)]
    [InlineData("   ", VolumeKind.Unlabeled)]
    [InlineData("ephemeral", VolumeKind.Ephemeral)]
    [InlineData(" ephemeral ", VolumeKind.Ephemeral)]
    [InlineData("business", VolumeKind.Business)]
    [InlineData("Business", VolumeKind.Business)]
    [InlineData("busines", VolumeKind.Business)]
    [InlineData("EPHEMERAL", VolumeKind.Business)]
    [InlineData("unknown", VolumeKind.Business)]
    public void Classify_MatchesContract(string? label, VolumeKind expected)
        => Assert.Equal(expected, VolumeClassifier.Classify(label));

    [Theory]
    [InlineData(VolumeKind.Unlabeled, "unlabeled")]
    [InlineData(VolumeKind.Ephemeral, "ephemeral")]
    [InlineData(VolumeKind.Business, "business")]
    public void ToApiValue_IsStable(VolumeKind kind, string expected)
        => Assert.Equal(expected, VolumeClassifier.ToApiValue(kind));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeLabelsTests|FullyQualifiedName~VolumeClassifierTests"`

Expected: 编译失败，提示 `OpenSandbox.Web.Infrastructure.Volumes` 类型不存在。

- [ ] **Step 3: 实现标签常量与规范化**

新建 `backend/Infrastructure/Volumes/VolumeLabels.cs`：

```csharp
using System.Text.RegularExpressions;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeLabels
{
    public const string Managed = "opensandbox.io/volume-managed";
    public const string Lifecycle = "opensandbox.io/volume-lifecycle";
    public const string Tenant = "opensandbox.io/volume-tenant";
    public const string Owner = "opensandbox.io/volume-owner";
    public const string ContractVersion = "opensandbox.io/volume-contract-version";
    public const string ContractVersionValue = "1";

    private static readonly Regex InvalidOwnerCharacters =
        new("[^A-Za-z0-9_.-]", RegexOptions.Compiled);

    private static readonly Regex ValidLabelValue =
        new("^[A-Za-z0-9]([-A-Za-z0-9_.]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    public static string? NormalizeOwner(string ownerKind, string ownerId)
    {
        var raw = $"{ownerKind}-{ownerId}".Trim();
        if (raw.Length == 0) return null;

        var normalized = InvalidOwnerCharacters.Replace(raw, "-");
        return normalized.Length <= 63 && ValidLabelValue.IsMatch(normalized)
            ? normalized
            : null;
    }
}
```

新建 `backend/Infrastructure/Volumes/VolumeValues.cs`：

```csharp
namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class VolumeLifecycle
{
    public const string Business = "business";
    public const string Ephemeral = "ephemeral";

    public static bool IsValid(string? value) => value is Business or Ephemeral;
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
```

新建 `backend/Infrastructure/Volumes/VolumeClassifier.cs`：

```csharp
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
        VolumeKind.Ephemeral => "ephemeral",
        VolumeKind.Business => "business",
        _ => "business",
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeLabelsTests|FullyQualifiedName~VolumeClassifierTests"`

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend/Infrastructure/Volumes/VolumeLabels.cs backend/Infrastructure/Volumes/VolumeValues.cs backend/Infrastructure/Volumes/VolumeClassifier.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelsTests.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeClassifierTests.cs
git commit -m "feat(volumes): add label contract and classifier" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 对账纯函数

**Files:**

- Create: `backend/Infrastructure/Volumes/VolumeReconciliation.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeReconciliationTests.cs`

**Interfaces:**

- Consumes: Task 1 的 `VolumeLabels`、`VolumeLifecycle`、`VolumeLabelStates`、`VolumeStates`、`VolumeEventTypes`
- Produces: `ObservedFacts(string Uid, IReadOnlyDictionary<string,string> Labels)`
- Produces: `ReconcileInput(...)`、`ReconcileResult(...)`、`VolumeReconciliation.Reconcile(ReconcileInput): ReconcileResult`
- Produces: `ReconcileResult.Violations` 中的内部标记 `reappeared-with-new-uid`，用于事件去重

- [ ] **Step 1: 写状态迁移失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeReconciliationTests.cs`：

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeReconciliationTests"`

Expected: 编译失败，提示 `VolumeReconciliation` 不存在。

- [ ] **Step 3: 实现对账函数**

新建 `backend/Infrastructure/Volumes/VolumeReconciliation.cs`：

```csharp
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
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeReconciliationTests"`

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend/Infrastructure/Volumes/VolumeReconciliation.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeReconciliationTests.cs
git commit -m "feat(volumes): add reconciliation state machine" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: EF Core 持久化

**Files:**

- Modify: `backend/OpenSandbox.Web.csproj`
- Create: `backend/Infrastructure/Volumes/Persistence/VolumeEntities.cs`
- Create: `backend/Infrastructure/Volumes/Persistence/JsonColumn.cs`
- Create: `backend/Infrastructure/Volumes/Persistence/VolumeDbContext.cs`
- Create: `backend/Infrastructure/Volumes/Persistence/VolumeDbContextFactory.cs`
- Create: `backend/Migrations/*`（由 `dotnet ef` 生成）
- Modify: `tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/TestDb.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeDbContextTests.cs`

**Interfaces:**

- Consumes: Task 1–2 的常量
- Produces: `VolumeDbContext`，DbSet：`Tenants`、`TenantNamespaces`、`TenantApiKeys`、`Volumes`、`ClusterVolumes`、`ManagedNamespaces`、`ScanRuns`、`VolumeEvents`
- Produces: 实体 `TenantEntity`、`TenantNamespaceEntity`、`TenantApiKeyEntity`、`VolumeEntity`、`ClusterVolumeEntity`、`ManagedNamespaceEntity`、`ScanRunEntity`、`VolumeEventEntity`
- Produces: `VolumeEntity.Violations()` / `SetViolations(IEnumerable<string>)`、`ClusterVolumeEntity.Labels()` / `MountedBy()`
- Produces (tests): `TestDb`（SQLite 内存库 + `EnsureCreated`）

- [ ] **Step 1: 安装包并写持久化失败测试**

Run（仓库根目录）:

```bash
dotnet add backend/OpenSandbox.Web.csproj package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add backend/OpenSandbox.Web.csproj package Microsoft.EntityFrameworkCore.Design
dotnet add tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj package Microsoft.EntityFrameworkCore.Sqlite
```

Expected: 三个项目文件出现对应的 `PackageReference`。

新建 `tests/OpenSandbox.Web.Tests/Volumes/TestDb.cs`：

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public VolumeDbContext Db { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new VolumeDbContext(options);
        Db.Database.EnsureCreated();

        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        Db.Tenants.AddRange(
            new TenantEntity
            {
                Id = "goodcrew", DisplayName = "GoodCrew", Status = "active",
                CreatedAt = now, UpdatedAt = now,
            },
            new TenantEntity
            {
                Id = "other", DisplayName = "Other", Status = "active",
                CreatedAt = now, UpdatedAt = now,
            });
        Db.SaveChanges();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeDbContextTests.cs`：

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeDbContextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static VolumeEntity Volume(string claimName = "pvc-1", string tenant = "goodcrew") => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = tenant,
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Role = "meta",
        MountPath = "/workspace",
        Lifecycle = VolumeLifecycle.Business,
        ContractVersion = VolumeLabels.ContractVersionValue,
        State = VolumeStates.Active,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now,
        UpdatedAt = Now,
    };

    [Fact]
    public async Task NamespaceAndClaimName_AreUnique()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Volume());
        await test.Db.SaveChangesAsync();

        test.Db.Volumes.Add(Volume(tenant: "other"));
        await Assert.ThrowsAsync<DbUpdateException>(() => test.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Namespace_BelongsToOnlyOneTenant()
    {
        using var test = new TestDb();
        test.Db.TenantNamespaces.Add(new TenantNamespaceEntity { Namespace = "opensandbox", TenantId = "goodcrew" });
        await test.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            test.Db.Database.ExecuteSqlRawAsync(
                "INSERT INTO tenant_namespaces (Namespace, TenantId) VALUES ('opensandbox', 'other')"));
    }

    [Fact]
    public void Violations_RoundTripAsSortedJson()
    {
        var volume = Volume();
        volume.SetViolations(["tenant-mismatch:other", "labels-missing"]);

        Assert.Equal("[\"labels-missing\",\"tenant-mismatch:other\"]", volume.ViolationsJson);
        Assert.True(volume.Violations().SetEquals(["labels-missing", "tenant-mismatch:other"]));
    }

    [Fact]
    public void ObservationHelpers_RoundTripJson()
    {
        var observation = new ClusterVolumeEntity
        {
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            LabelsJson = "{\"opensandbox.io/volume-lifecycle\":\"business\"}",
            MountedByJson = "[\"pod-1\"]",
        };

        Assert.Equal("business", observation.Labels()[VolumeLabels.Lifecycle]);
        Assert.Equal(["pod-1"], observation.MountedBy());
    }

    [Fact]
    public async Task LedgerRows_AreNotDeletedByObservationCleanup()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Volume());
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox",
            ClaimName = "pvc-1",
            Uid = "uid-1",
            Phase = "Bound",
            CreatedAtK8s = Now,
            FirstSeenAt = Now,
            LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        test.Db.ClusterVolumes.RemoveRange(test.Db.ClusterVolumes);
        await test.Db.SaveChangesAsync();

        Assert.Equal(1, await test.Db.Volumes.CountAsync());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeDbContextTests"`

Expected: 编译失败，提示 `VolumeDbContext` / 实体不存在。

- [ ] **Step 3: 实现实体、JSON 辅助与 DbContext**

新建 `backend/Infrastructure/Volumes/Persistence/JsonColumn.cs`：

```csharp
using System.Text.Json;

namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public static class JsonColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string? json, T fallback) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
```

新建 `backend/Infrastructure/Volumes/Persistence/VolumeEntities.cs`：

```csharp
namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public sealed class TenantEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<TenantNamespaceEntity> Namespaces { get; set; } = [];
    public List<TenantApiKeyEntity> ApiKeys { get; set; } = [];
}

public sealed class TenantNamespaceEntity
{
    public string Namespace { get; set; } = "";
    public string TenantId { get; set; } = "";
    public TenantEntity Tenant { get; set; } = null!;
}

public sealed class TenantApiKeyEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public string Prefix { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public TenantEntity Tenant { get; set; } = null!;
}

public sealed class VolumeEntity
{
    public Guid Id { get; set; }
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string OwnerKind { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string? Role { get; set; }
    public string? MountPath { get; set; }
    public string Lifecycle { get; set; } = VolumeLifecycle.Business;
    public string ContractVersion { get; set; } = VolumeLabels.ContractVersionValue;
    public string? LastSandboxId { get; set; }
    public string? RegisteredUid { get; set; }
    public string State { get; set; } = VolumeStates.Active;
    public string? StateReason { get; set; }
    public DateTimeOffset? MissingSince { get; set; }
    public string LabelState { get; set; } = VolumeLabelStates.Pending;
    public string ViolationsJson { get; set; } = "[]";
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    public IReadOnlySet<string> Violations() =>
        JsonColumn.Read<HashSet<string>>(ViolationsJson, new())!;

    public void SetViolations(IEnumerable<string> values) =>
        ViolationsJson = JsonColumn.Write(
            values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
}

public sealed class ClusterVolumeEntity
{
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Phase { get; set; } = "";
    public string? StorageClass { get; set; }
    public string? RequestedStorage { get; set; }
    public string? Capacity { get; set; }
    public DateTimeOffset CreatedAtK8s { get; set; }
    public DateTimeOffset? DeletionTimestamp { get; set; }
    public bool Mounted { get; set; }
    public string MountedByJson { get; set; } = "[]";
    public string LabelsJson { get; set; } = "{}";
    public string? LifecycleLabel { get; set; }
    public string? TenantLabel { get; set; }
    public string? OwnerLabel { get; set; }
    public string? ContractVersionLabel { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid LastScanId { get; set; }

    public IReadOnlyDictionary<string, string> Labels() =>
        JsonColumn.Read<Dictionary<string, string>>(LabelsJson, new())!;

    public IReadOnlyList<string> MountedBy() =>
        JsonColumn.Read<List<string>>(MountedByJson, [])!;
}

public sealed class ManagedNamespaceEntity
{
    public string Namespace { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string State { get; set; } = ManagedNamespaceStates.Active;
}

public sealed class ScanRunEntity
{
    public Guid Id { get; set; }
    public string Trigger { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int NamespacesScanned { get; set; }
    public int NamespacesSucceeded { get; set; }
    public int VolumeCount { get; set; }
    public string ErrorsJson { get; set; } = "[]";
}

public sealed class VolumeEventEntity
{
    public Guid Id { get; set; }
    public Guid? VolumeId { get; set; }
    public string Namespace { get; set; } = "";
    public string ClaimName { get; set; } = "";
    public string? TenantId { get; set; }
    public string Type { get; set; } = "";
    public string Actor { get; set; } = "";
    public string? Reason { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
```

新建 `backend/Infrastructure/Volumes/Persistence/VolumeDbContext.cs`：

```csharp
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
            entity.Property(x => x.ViolationsJson).HasColumnType(jsonType);
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
            entity.Property(x => x.MountedByJson).HasColumnType(jsonType);
            entity.Property(x => x.LabelsJson).HasColumnType(jsonType);
            entity.Property(x => x.LifecycleLabel).HasMaxLength(64);
            entity.Property(x => x.TenantLabel).HasMaxLength(253);
            entity.Property(x => x.OwnerLabel).HasMaxLength(64);
            entity.Property(x => x.ContractVersionLabel).HasMaxLength(16);
            entity.HasIndex(x => x.Uid);
            entity.HasIndex(x => x.LastScanId);
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
            entity.Property(x => x.ErrorsJson).HasColumnType(jsonType);
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
            entity.Property(x => x.Actor).HasMaxLength(64);
            entity.Property(x => x.Reason).HasMaxLength(512);
            entity.Property(x => x.DataJson).HasColumnType(jsonType);
            entity.HasIndex(x => new { x.Namespace, x.ClaimName, x.CreatedAt });
            entity.HasIndex(x => x.VolumeId);
        });
    }
}
```

新建 `backend/Infrastructure/Volumes/Persistence/VolumeDbContextFactory.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public sealed class VolumeDbContextFactory : IDesignTimeDbContextFactory<VolumeDbContext>
{
    public VolumeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__VolumeManager")
            ?? "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=opensandbox";

        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new VolumeDbContext(options);
    }
}
```

- [ ] **Step 4: 生成初始迁移**

Run:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialVolumeManager --project backend --startup-project backend --output-dir Migrations
```

Expected: `backend/Migrations/` 下生成 `<timestamp>_InitialVolumeManager.cs`、`VolumeDbContextModelSnapshot.cs` 与 Designer 文件；迁移包含 `tenants`、`tenant_namespaces`、`tenant_api_keys`、`volumes`、`cluster_volumes`、`managed_namespaces`、`scan_runs`、`volume_events`。

- [ ] **Step 5: 运行持久化测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeDbContextTests"`

Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add backend/OpenSandbox.Web.csproj backend/Migrations backend/Infrastructure/Volumes/Persistence tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj tests/OpenSandbox.Web.Tests/Volumes/TestDb.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeDbContextTests.cs
git commit -m "feat(volumes): add persistence model and migration" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: Kubernetes 客户端与标签写入

**Files:**

- Modify: `backend/OpenSandbox.Web.csproj`
- Create: `backend/Infrastructure/Volumes/IVolumeKubernetesClient.cs`
- Create: `backend/Infrastructure/Volumes/KubernetesVolumeClient.cs`
- Create: `backend/Infrastructure/Volumes/VolumeManagerOptions.cs`
- Create: `backend/Infrastructure/Volumes/VolumeLabelWriter.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/FakeVolumeKubernetesClient.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelWriterTests.cs`

**Interfaces:**

- Produces: `ManagedNamespaceInfo(string Name, IReadOnlyDictionary<string,string> Labels)`
- Produces: `NamespaceInfo(string Name, IReadOnlyDictionary<string,string> Labels)`
- Produces: `ObservedPvc(...)`、`PodMount(string PodName, IReadOnlyList<string> ClaimNames)`
- Produces: `IVolumeKubernetesClient` 的 `ListManagedNamespacesAsync`、`GetNamespaceAsync`、`ListPvcsAsync`、`ListPodMountsAsync`、`PatchPvcLabelsAsync`
- Produces: `VolumeManagerOptions`（`SectionName = "VolumeManager"`）
- Produces: `VolumeLabelWriter.BuildAllLabels(VolumeEntity)`、`ApplyAllAsync`、`ApplyLifecycleAsync`、`LabelWriteResult(bool Success, string? Error)`
- Produces (tests): `FakeVolumeKubernetesClient`

- [ ] **Step 1: 安装 KubernetesClient 并写标签写入失败测试**

Run:

```bash
dotnet add backend/OpenSandbox.Web.csproj package KubernetesClient
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/FakeVolumeKubernetesClient.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class FakeVolumeKubernetesClient : IVolumeKubernetesClient
{
    public Dictionary<string, NamespaceInfo> Namespaces { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<ObservedPvc>> Pvcs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<PodMount>> Pods { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailNamespaceLookups { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailPvcLists { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailPodLists { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailLabelPatches { get; } = new(StringComparer.Ordinal);
    public List<(string Namespace, string ClaimName, IReadOnlyDictionary<string, string> Labels)> Patches { get; } = [];
    public Exception? ListNamespacesException { get; set; }

    public static FakeVolumeKubernetesClient WithManagedNamespace(string name = "opensandbox")
    {
        var fake = new FakeVolumeKubernetesClient();
        fake.Namespaces[name] = new NamespaceInfo(name, new Dictionary<string, string>
        {
            [VolumeLabels.Managed] = "true",
        });
        fake.Pvcs[name] = [];
        fake.Pods[name] = [];
        return fake;
    }

    public void AddPvc(
        string namespaceName,
        string claimName,
        string uid = "uid-1",
        Dictionary<string, string>? labels = null,
        DateTimeOffset? deletionTimestamp = null)
    {
        if (!Pvcs.TryGetValue(namespaceName, out var pvcs))
            Pvcs[namespaceName] = pvcs = [];
        pvcs.Add(new ObservedPvc(
            namespaceName, claimName, uid, "Bound", "standard", "1Gi", "1Gi",
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), deletionTimestamp,
            labels ?? new Dictionary<string, string>()));
    }

    public Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct)
    {
        if (ListNamespacesException is not null) throw ListNamespacesException;
        IReadOnlyList<ManagedNamespaceInfo> result = Namespaces.Values
            .Where(ns => ns.Labels.TryGetValue(VolumeLabels.Managed, out var value) && value == "true")
            .Select(ns => new ManagedNamespaceInfo(ns.Name, ns.Labels))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct)
    {
        if (FailNamespaceLookups.Contains(name)) throw new InvalidOperationException("namespace lookup failed");
        return Task.FromResult(Namespaces.GetValueOrDefault(name));
    }

    public Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string ns, CancellationToken ct)
    {
        if (FailPvcLists.Contains(ns)) throw new InvalidOperationException("pvc list failed");
        return Task.FromResult((IReadOnlyList<ObservedPvc>)Pvcs.GetValueOrDefault(ns) ?? []);
    }

    public Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string ns, CancellationToken ct)
    {
        if (FailPodLists.Contains(ns)) throw new InvalidOperationException("pod list failed");
        return Task.FromResult((IReadOnlyList<PodMount>)Pods.GetValueOrDefault(ns) ?? []);
    }

    public Task PatchPvcLabelsAsync(
        string ns, string claimName, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (FailLabelPatches.Contains(claimName)) throw new InvalidOperationException("patch failed");
        Patches.Add((ns, claimName, labels));
        return Task.CompletedTask;
    }
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelWriterTests.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeLabelWriterTests
{
    private static VolumeEntity Volume() => new()
    {
        Namespace = "opensandbox",
        ClaimName = "pvc-1",
        TenantId = "goodcrew",
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = VolumeLifecycle.Business,
    };

    [Fact]
    public void BuildAllLabels_IncludesOwnerOnlyWhenNormalizable()
    {
        var labels = VolumeLabelWriter.BuildAllLabels(Volume());

        Assert.Equal("business", labels[VolumeLabels.Lifecycle]);
        Assert.Equal("goodcrew", labels[VolumeLabels.Tenant]);
        Assert.Equal("1", labels[VolumeLabels.ContractVersion]);
        Assert.Equal("employee-3f2a", labels[VolumeLabels.Owner]);

        var invalid = Volume();
        invalid.OwnerId = "!!!";
        Assert.False(VolumeLabelWriter.BuildAllLabels(invalid).ContainsKey(VolumeLabels.Owner));
    }

    [Fact]
    public async Task ApplyAllAsync_WritesContractLabels()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyAllAsync(Volume(), CancellationToken.None);

        Assert.True(result.Success);
        var patch = Assert.Single(fake.Patches);
        Assert.Equal("pvc-1", patch.ClaimName);
        Assert.Equal("goodcrew", patch.Labels[VolumeLabels.Tenant]);
    }

    [Fact]
    public async Task ApplyLifecycleAsync_TouchesOnlyLifecycleLabel()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyLifecycleAsync(Volume(), VolumeLifecycle.Ephemeral, CancellationToken.None);

        Assert.True(result.Success);
        var patch = Assert.Single(fake.Patches);
        Assert.Equal(["opensandbox.io/volume-lifecycle"], patch.Labels.Keys);
        Assert.Equal("ephemeral", patch.Labels[VolumeLabels.Lifecycle]);
    }

    [Fact]
    public async Task ApplyAllAsync_ReturnsFailureInsteadOfThrowing()
    {
        var fake = FakeVolumeKubernetesClient.WithManagedNamespace();
        fake.FailLabelPatches.Add("pvc-1");
        var writer = new VolumeLabelWriter(fake);

        var result = await writer.ApplyAllAsync(Volume(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("patch failed", result.Error);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeLabelWriterTests"`

Expected: 编译失败，提示 `VolumeLabelWriter` / `IVolumeKubernetesClient` 不存在。

- [ ] **Step 3: 实现 K8s 客户端、选项与标签写入**

新建 `backend/Infrastructure/Volumes/VolumeManagerOptions.cs`：

```csharp
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
```

新建 `backend/Infrastructure/Volumes/IVolumeKubernetesClient.cs`：

```csharp
namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record ManagedNamespaceInfo(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

public sealed record NamespaceInfo(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ObservedPvc(
    string Namespace,
    string ClaimName,
    string Uid,
    string Phase,
    string? StorageClass,
    string? RequestedStorage,
    string? Capacity,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels);

public sealed record PodMount(
    string PodName,
    IReadOnlyList<string> ClaimNames);

public interface IVolumeKubernetesClient
{
    Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct);

    Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string namespaceName, CancellationToken ct);

    Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string namespaceName, CancellationToken ct);

    Task PatchPvcLabelsAsync(
        string namespaceName,
        string claimName,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct);
}
```

新建 `backend/Infrastructure/Volumes/KubernetesVolumeClient.cs`：

```csharp
using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class KubernetesVolumeClient : IVolumeKubernetesClient
{
    private readonly IKubernetes _client;
    private readonly VolumeManagerOptions _options;

    public KubernetesVolumeClient(IKubernetes client, VolumeManagerOptions options)
    {
        _client = client;
        _options = options;
    }

    public async Task<IReadOnlyList<ManagedNamespaceInfo>> ListManagedNamespacesAsync(CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespaceAsync(
            labelSelector: _options.ManagedNamespaceSelector,
            cancellationToken: ct);
        return list.Items.Select(MapManagedNamespace).ToArray();
    }

    public async Task<NamespaceInfo?> GetNamespaceAsync(string name, CancellationToken ct)
    {
        try
        {
            return MapNamespaceInfo(await _client.CoreV1.ReadNamespaceAsync(name, cancellationToken: ct));
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ObservedPvc>> ListPvcsAsync(string namespaceName, CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
            namespaceName, cancellationToken: ct);
        return list.Items.Select(MapPvc).ToArray();
    }

    public async Task<IReadOnlyList<PodMount>> ListPodMountsAsync(string namespaceName, CancellationToken ct)
    {
        var list = await _client.CoreV1.ListNamespacedPodAsync(namespaceName, cancellationToken: ct);
        return list.Items
            .Where(pod => pod.Status?.Phase is not ("Succeeded" or "Failed"))
            .Select(pod => new PodMount(
                pod.Metadata.Name,
                pod.Spec?.Volumes?
                    .Where(volume => volume.PersistentVolumeClaim is not null)
                    .Select(volume => volume.PersistentVolumeClaim.ClaimName)
                    .ToArray() ?? []))
            .Where(mount => mount.ClaimNames.Count > 0)
            .ToArray();
    }

    public async Task PatchPvcLabelsAsync(
        string namespaceName,
        string claimName,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { metadata = new { labels } });
        var patch = new V1Patch(json, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(
            patch, claimName, namespaceName, cancellationToken: ct);
    }

    private static ManagedNamespaceInfo MapManagedNamespace(V1Namespace ns) =>
        new(ns.Metadata.Name, ns.Metadata.Labels is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ns.Metadata.Labels));

    private static NamespaceInfo MapNamespaceInfo(V1Namespace ns) =>
        new(ns.Metadata.Name, ns.Metadata.Labels is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ns.Metadata.Labels));

    private static ObservedPvc MapPvc(V1PersistentVolumeClaim pvc)
    {
        var metadata = pvc.Metadata;
        return new ObservedPvc(
            metadata.NamespaceProperty ?? "",
            metadata.Name,
            metadata.Uid ?? "",
            pvc.Status?.Phase ?? "Unknown",
            pvc.Spec?.StorageClassName,
            Storage(pvc.Spec?.Resources?.Requests),
            Storage(pvc.Status?.Capacity),
            ToOffset(metadata.CreationTimestamp) ?? DateTimeOffset.UnixEpoch,
            ToOffset(metadata.DeletionTimestamp),
            metadata.Labels is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata.Labels));
    }

    private static string? Storage(IDictionary<string, ResourceQuantity>? values) =>
        values is not null && values.TryGetValue("storage", out var quantity)
            ? quantity.ToString()
            : null;

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
```

新建 `backend/Infrastructure/Volumes/VolumeLabelWriter.cs`：

```csharp
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record LabelWriteResult(bool Success, string? Error = null);

public sealed class VolumeLabelWriter(IVolumeKubernetesClient kube)
{
    public static IReadOnlyDictionary<string, string> BuildAllLabels(VolumeEntity volume)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VolumeLabels.Lifecycle] = volume.Lifecycle,
            [VolumeLabels.Tenant] = volume.TenantId,
            [VolumeLabels.ContractVersion] = volume.ContractVersion,
        };

        var owner = VolumeLabels.NormalizeOwner(volume.OwnerKind, volume.OwnerId);
        if (owner is not null)
            labels[VolumeLabels.Owner] = owner;

        return labels;
    }

    public Task<LabelWriteResult> ApplyAllAsync(VolumeEntity volume, CancellationToken ct) =>
        WriteAsync(volume, BuildAllLabels(volume), ct);

    public Task<LabelWriteResult> ApplyLifecycleAsync(
        VolumeEntity volume, string lifecycle, CancellationToken ct) =>
        WriteAsync(volume, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VolumeLabels.Lifecycle] = lifecycle,
        }, ct);

    private async Task<LabelWriteResult> WriteAsync(
        VolumeEntity volume,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct)
    {
        try
        {
            await kube.PatchPvcLabelsAsync(volume.Namespace, volume.ClaimName, labels, ct);
            return new LabelWriteResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LabelWriteResult(false, exception.Message);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~VolumeLabelWriterTests"`

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend/OpenSandbox.Web.csproj backend/Infrastructure/Volumes/IVolumeKubernetesClient.cs backend/Infrastructure/Volumes/KubernetesVolumeClient.cs backend/Infrastructure/Volumes/VolumeManagerOptions.cs backend/Infrastructure/Volumes/VolumeLabelWriter.cs tests/OpenSandbox.Web.Tests/Volumes/FakeVolumeKubernetesClient.cs tests/OpenSandbox.Web.Tests/Volumes/VolumeLabelWriterTests.cs
git commit -m "feat(volumes): add kubernetes client and label writer" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 盘点扫描器

**Files:**

- Create: `backend/Infrastructure/Volumes/InventoryScanService.cs`
- Create: `backend/Infrastructure/Volumes/ScanCoordinator.cs`
- Create: `backend/Infrastructure/Volumes/InventoryScanner.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/FixedTimeProvider.cs`
- Create: `tests/OpenSandbox.Web.Tests/Volumes/InventoryScanServiceTests.cs`

**Interfaces:**

- Consumes: Task 2 的 `VolumeReconciliation`、Task 3 的 `VolumeDbContext`、Task 4 的 `IVolumeKubernetesClient` / `VolumeLabelWriter` / `VolumeManagerOptions`
- Produces: `ScanSummary(Guid ScanId, string Trigger, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int NamespacesScanned, int NamespacesSucceeded, int VolumeCount, IReadOnlyList<ScanError> Errors)`
- Produces: `ScanCoordinator.ScanAsync(string trigger, CancellationToken): Task<ScanSummary>`，同时只允许一轮
- Produces: `ScanInProgressException`
- Produces: `InventoryScanner : BackgroundService`

- [ ] **Step 1: 写扫描器失败测试**

新建 `tests/OpenSandbox.Web.Tests/Volumes/FixedTimeProvider.cs`：

```csharp
namespace OpenSandbox.Web.Tests.Volumes;

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
```

新建 `tests/OpenSandbox.Web.Tests/Volumes/InventoryScanServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Infrastructure.Volumes;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class InventoryScanServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    private static VolumeEntity Ledger(
        string claimName,
        string lifecycle = VolumeLifecycle.Business,
        string state = VolumeStates.Active,
        string? registeredUid = "uid-1",
        DateTimeOffset? lastSeenAt = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = "opensandbox",
        ClaimName = claimName,
        TenantId = "goodcrew",
        OwnerKind = "employee",
        OwnerId = "3f2a",
        Lifecycle = lifecycle,
        ContractVersion = VolumeLabels.ContractVersionValue,
        RegisteredUid = registeredUid,
        State = state,
        LabelState = VolumeLabelStates.Applied,
        RegisteredAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
        LastSeenAt = lastSeenAt,
    };

    private static Dictionary<string, string> Labels(
        string lifecycle = "business", string tenant = "goodcrew") => new()
    {
        [VolumeLabels.Lifecycle] = lifecycle,
        [VolumeLabels.Tenant] = tenant,
        [VolumeLabels.Owner] = "employee-3f2a",
        [VolumeLabels.ContractVersion] = "1",
    };

    private static InventoryScanService Service(
        TestDb test,
        FakeVolumeKubernetesClient kube,
        VolumeManagerOptions? options = null) =>
        new(test.Db, kube, new VolumeLabelWriter(kube),
            options ?? new VolumeManagerOptions { MaxConcurrentNamespaces = 2 },
            new FixedTimeProvider(Now), NullLogger<InventoryScanService>.Instance);

    [Fact]
    public async Task Scan_UpsertsObservationsAndMounts()
    {
        using var test = new TestDb();
        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());
        kube.AddPvc("opensandbox", "pvc-2", uid: "uid-2", labels: Labels());
        kube.Pods["opensandbox"].Add(new PodMount("pod-1", ["pvc-1"]));
        kube.Pods["opensandbox"].Add(new PodMount("pod-done", ["pvc-2"]));
        kube.Pods["opensandbox"] = kube.Pods["opensandbox"]
            .Where(p => p.PodName != "pod-done").ToList();

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(1, summary.NamespacesSucceeded);
        Assert.Equal(2, summary.VolumeCount);
        var one = await test.Db.ClusterVolumes.SingleAsync(x => x.ClaimName == "pvc-1");
        Assert.True(one.Mounted);
        Assert.Equal(["pod-1"], one.MountedBy());
        Assert.Equal("business", one.LifecycleLabel);
    }

    [Fact]
    public async Task FailedNamespaceList_DoesNotTransitionOrDeleteObservations()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox", ClaimName = "pvc-1", Uid = "uid-1",
            Phase = "Bound", CreatedAtK8s = Now, FirstSeenAt = Now, LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.FailPvcLists.Add("opensandbox");

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Single(summary.Errors);
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.Equal(VolumeStates.Active, (await test.Db.Volumes.SingleAsync()).State);
        Assert.Equal(1, await test.Db.ClusterVolumes.CountAsync());
    }

    [Fact]
    public async Task BusinessVolumeDisappears_RecordsUnexpectedEvent()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        await test.Db.SaveChangesAsync();

        var summary = await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeStates.Unexpected, volume.StateReason);
        Assert.Equal(1, summary.NamespacesSucceeded);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task NamespaceDeleted_MarksMissingWithoutPerRowUnexpectedEvents()
    {
        using var test = new TestDb();
        test.Db.Volumes.Add(Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5)));
        test.Db.ManagedNamespaces.Add(new ManagedNamespaceEntity
        {
            Namespace = "opensandbox", FirstSeenAt = Now, LastSeenAt = Now,
            State = ManagedNamespaceStates.Active,
        });
        await test.Db.SaveChangesAsync();

        var kube = new FakeVolumeKubernetesClient();

        var summary = await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        var volume = await test.Db.Volumes.SingleAsync();
        Assert.Equal(VolumeStates.Missing, volume.State);
        Assert.Equal(VolumeStates.NamespaceDeleted, volume.StateReason);
        Assert.Equal(0, summary.NamespacesSucceeded);
        Assert.True(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.NamespaceMassDisappearance));
        Assert.False(await test.Db.VolumeEvents.AnyAsync(e =>
            e.Type == VolumeEventTypes.UnexpectedDisappearance));
    }

    [Fact]
    public async Task PendingLabel_IsRetriedDuringScan()
    {
        using var test = new TestDb();
        var volume = Ledger("pvc-1", lastSeenAt: Now.AddMinutes(-5));
        volume.LabelState = VolumeLabelStates.Pending;
        test.Db.Volumes.Add(volume);
        await test.Db.SaveChangesAsync();

        var kube = FakeVolumeKubernetesClient.WithManagedNamespace();
        kube.AddPvc("opensandbox", "pvc-1", labels: Labels());

        await Service(test, kube).ScanAsync("manual", CancellationToken.None);

        Assert.Equal(VolumeLabelStates.Applied, (await test.Db.Volumes.SingleAsync()).LabelState);
        Assert.Single(kube.Patches);
    }

    [Fact]
    public async Task RemovedPvc_DeletesObservationRow()
    {
        using var test = new TestDb();
        test.Db.ClusterVolumes.Add(new ClusterVolumeEntity
        {
            Namespace = "opensandbox", ClaimName = "pvc-gone", Uid = "uid-9",
            Phase = "Bound", CreatedAtK8s = Now, FirstSeenAt = Now, LastSeenAt = Now,
            LastScanId = Guid.CreateVersion7(),
        });
        await test.Db.SaveChangesAsync();

        await Service(test, FakeVolumeKubernetesClient.WithManagedNamespace())
            .ScanAsync("manual", CancellationToken.None);

        Assert.Equal(0, await test.Db.ClusterVolumes.CountAsync());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~InventoryScanServiceTests"`

Expected: 编译失败，提示 `InventoryScanService` 不存在。

- [ ] **Step 3: 实现扫描服务、协调器与后台 Worker**

新建 `backend/Infrastructure/Volumes/InventoryScanService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed record ScanError(string Namespace, string Message);

public sealed record ScanSummary(
    Guid ScanId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int NamespacesScanned,
    int NamespacesSucceeded,
    int VolumeCount,
    IReadOnlyList<ScanError> Errors);

internal sealed record NamespaceFetch(
    string Namespace,
    IReadOnlyList<ObservedPvc> Pvcs,
    IReadOnlyList<PodMount> Pods,
    string? Error);

public sealed class InventoryScanService(
    VolumeDbContext db,
    IVolumeKubernetesClient kube,
    VolumeLabelWriter labels,
    VolumeManagerOptions options,
    TimeProvider time,
    ILogger<InventoryScanService> logger)
{
    public async Task<ScanSummary> ScanAsync(string trigger, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var scan = new ScanRunEntity
        {
            Id = Guid.CreateVersion7(),
            Trigger = trigger,
            StartedAt = now,
        };
        db.ScanRuns.Add(scan);
        await db.SaveChangesAsync(ct);

        var errors = new List<ScanError>();

        IReadOnlyList<ManagedNamespaceInfo> discovered;
        try
        {
            discovered = await kube.ListManagedNamespacesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(new ScanError("*", exception.Message));
            await FinishAsync(scan, 0, 0, 0, errors, ct);
            return ToSummary(scan);
        }

        var managed = await db.ManagedNamespaces
            .ToDictionaryAsync(x => x.Namespace, StringComparer.Ordinal, ct);
        var discoveredNames = discovered.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var ns in discovered)
        {
            if (managed.TryGetValue(ns.Name, out var row))
            {
                row.State = ManagedNamespaceStates.Active;
                row.LastSeenAt = now;
            }
            else
            {
                db.ManagedNamespaces.Add(new ManagedNamespaceEntity
                {
                    Namespace = ns.Name,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    State = ManagedNamespaceStates.Active,
                });
            }
        }

        foreach (var row in managed.Values.Where(x =>
                     !discoveredNames.Contains(x.Namespace) &&
                     x.State == ManagedNamespaceStates.Active))
        {
            NamespaceInfo? namespaceInfo;
            try
            {
                namespaceInfo = await kube.GetNamespaceAsync(row.Namespace, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new ScanError(row.Namespace, exception.Message));
                continue;
            }

            var volumes = await db.Volumes
                .Where(volume => volume.Namespace == row.Namespace &&
                                 volume.State != VolumeStates.Missing &&
                                 volume.State != VolumeStates.Unmanaged)
                .ToListAsync(ct);

            if (namespaceInfo is null)
            {
                foreach (var volume in volumes)
                {
                    volume.State = VolumeStates.Missing;
                    volume.StateReason = VolumeStates.NamespaceDeleted;
                    volume.MissingSince ??= now;
                    volume.UpdatedAt = now;
                }

                if (volumes.Count > 0)
                {
                    db.VolumeEvents.Add(BatchEvent(
                        row.Namespace,
                        VolumeEventTypes.NamespaceMassDisappearance,
                        now,
                        new { count = volumes.Count, reason = VolumeStates.NamespaceDeleted }));
                }

                row.State = ManagedNamespaceStates.Gone;
            }
            else
            {
                foreach (var volume in volumes)
                {
                    volume.State = VolumeStates.Unmanaged;
                    volume.StateReason = null;
                    volume.UpdatedAt = now;
                }

                if (volumes.Count > 0)
                {
                    db.VolumeEvents.Add(BatchEvent(
                        row.Namespace,
                        VolumeEventTypes.NamespaceUnmanaged,
                        now,
                        new { count = volumes.Count }));
                }

                row.State = ManagedNamespaceStates.Unmanaged;
            }
        }

        await db.SaveChangesAsync(ct);

        var fetched = new List<NamespaceFetch>();
        foreach (var chunk in discovered.Select(x => x.Name)
                     .Chunk(Math.Max(1, options.MaxConcurrentNamespaces)))
        {
            fetched.AddRange(await Task.WhenAll(chunk.Select(name => FetchAsync(name, ct))));
        }

        var scanned = 0;
        var succeeded = 0;

        foreach (var fetch in fetched)
        {
            scanned++;
            if (fetch.Error is not null)
            {
                errors.Add(new ScanError(fetch.Namespace, fetch.Error));
                continue;
            }

            await ApplyNamespaceAsync(fetch, scan.Id, now, ct);
            succeeded++;
        }

        var volumeCount = await db.ClusterVolumes.CountAsync(ct);
        await FinishAsync(scan, scanned, succeeded, volumeCount, errors, ct);
        return ToSummary(scan);
    }

    private async Task<NamespaceFetch> FetchAsync(string namespaceName, CancellationToken ct)
    {
        try
        {
            var pvcsTask = kube.ListPvcsAsync(namespaceName, ct);
            var podsTask = kube.ListPodMountsAsync(namespaceName, ct);
            await Task.WhenAll(pvcsTask, podsTask);
            return new NamespaceFetch(
                namespaceName, await pvcsTask, await podsTask, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "扫描命名空间 {Namespace} 失败", namespaceName);
            return new NamespaceFetch(namespaceName, [], [], exception.Message);
        }
    }

    private async Task ApplyNamespaceAsync(
        NamespaceFetch fetch,
        Guid scanId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pvcs = fetch.Pvcs.ToDictionary(x => x.ClaimName, StringComparer.Ordinal);
        var mountedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pod in fetch.Pods)
        {
            foreach (var claim in pod.ClaimNames)
            {
                if (!mountedBy.TryGetValue(claim, out var pods))
                    mountedBy[claim] = pods = [];
                pods.Add(pod.PodName);
            }
        }

        var observations = await db.ClusterVolumes
            .Where(row => row.Namespace == fetch.Namespace)
            .ToDictionaryAsync(row => row.ClaimName, StringComparer.Ordinal, ct);

        foreach (var (claimName, pvc) in pvcs)
        {
            var mounts = mountedBy.GetValueOrDefault(claimName) ?? [];
            if (observations.TryGetValue(claimName, out var observation))
            {
                if (pvc.DeletionTimestamp is not null && observation.DeletionTimestamp is null)
                {
                    db.VolumeEvents.Add(ObservationEvent(
                        fetch.Namespace, claimName,
                        VolumeEventTypes.TerminatingObserved, now));
                }

                UpdateObservation(observation, pvc, mounts, scanId, now);
            }
            else
            {
                var row = new ClusterVolumeEntity
                {
                    Namespace = fetch.Namespace,
                    ClaimName = claimName,
                    FirstSeenAt = now,
                };
                UpdateObservation(row, pvc, mounts, scanId, now);
                db.ClusterVolumes.Add(row);

                if (pvc.DeletionTimestamp is not null)
                {
                    db.VolumeEvents.Add(ObservationEvent(
                        fetch.Namespace, claimName,
                        VolumeEventTypes.TerminatingObserved, now));
                }
            }
        }

        foreach (var stale in observations.Values.Where(row => !pvcs.ContainsKey(row.ClaimName)))
            db.ClusterVolumes.Remove(stale);

        var volumes = await db.Volumes
            .Where(volume => volume.Namespace == fetch.Namespace)
            .ToListAsync(ct);

        var missingCount = 0;

        foreach (var volume in volumes)
        {
            var pvc = pvcs.GetValueOrDefault(volume.ClaimName);
            var observed = pvc is null ? null : new ObservedFacts(pvc.Uid, pvc.Labels);
            var wasActive = volume.State == VolumeStates.Active;

            var result = VolumeReconciliation.Reconcile(new ReconcileInput(
                TenantId: volume.TenantId,
                Lifecycle: volume.Lifecycle,
                State: volume.State,
                StateReason: volume.StateReason,
                RegisteredUid: volume.RegisteredUid,
                LastSeenAt: volume.LastSeenAt,
                LabelState: volume.LabelState,
                Violations: volume.Violations(),
                ExpectedOwnerLabel: VolumeLabels.NormalizeOwner(volume.OwnerKind, volume.OwnerId),
                Observed: observed,
                NamespaceScanSucceeded: true));

            volume.State = result.State;
            volume.StateReason = result.StateReason;
            volume.SetViolations(result.Violations);
            volume.UpdatedAt = now;

            if (result.ObservedThisScan)
                volume.LastSeenAt = now;
            if (result.State == VolumeStates.Active)
                volume.MissingSince = null;
            else
                volume.MissingSince ??= now;

            if (wasActive && result.State == VolumeStates.Missing)
                missingCount++;

            foreach (var eventType in result.Events)
            {
                AddVolumeEvent(volume, eventType, "scanner", now, result.StateReason,
                    new { violations = result.Violations });
            }

            if (volume.LabelState == VolumeLabelStates.Pending && pvc is not null)
            {
                var write = await labels.ApplyAllAsync(volume, ct);
                if (write.Success)
                {
                    volume.LabelState = VolumeLabelStates.Applied;
                    AddVolumeEvent(volume, VolumeEventTypes.LabelApplied, "scanner", now);
                }
                else
                {
                    AddVolumeEvent(volume, VolumeEventTypes.LabelPatchFailed, "scanner", now, write.Error);
                }
            }
        }

        var threshold = Math.Max(3, (int)Math.Ceiling(volumes.Count * 0.5));
        if (volumes.Count > 0 && missingCount >= threshold)
        {
            db.VolumeEvents.Add(BatchEvent(
                fetch.Namespace,
                VolumeEventTypes.NamespaceMassDisappearance,
                now,
                new { count = missingCount, threshold }));
        }

        await db.SaveChangesAsync(ct);
    }

    private static void UpdateObservation(
        ClusterVolumeEntity row,
        ObservedPvc pvc,
        IReadOnlyList<string> mountedBy,
        Guid scanId,
        DateTimeOffset now)
    {
        row.Uid = pvc.Uid;
        row.Phase = pvc.Phase;
        row.StorageClass = pvc.StorageClass;
        row.RequestedStorage = pvc.RequestedStorage;
        row.Capacity = pvc.Capacity;
        row.CreatedAtK8s = pvc.CreatedAt;
        row.DeletionTimestamp = pvc.DeletionTimestamp;
        row.Mounted = mountedBy.Count > 0;
        row.MountedByJson = JsonColumn.Write(
            mountedBy.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        row.LabelsJson = JsonColumn.Write(pvc.Labels);
        row.LifecycleLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Lifecycle);
        row.TenantLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Tenant);
        row.OwnerLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.Owner);
        row.ContractVersionLabel = pvc.Labels.GetValueOrDefault(VolumeLabels.ContractVersion);
        row.LastScanId = scanId;
        row.LastSeenAt = now;
    }

    private void AddVolumeEvent(
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

    private static VolumeEventEntity ObservationEvent(
        string namespaceName,
        string claimName,
        string type,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = namespaceName,
        ClaimName = claimName,
        Type = type,
        Actor = "scanner",
        DataJson = "{}",
        CreatedAt = now,
    };

    private static VolumeEventEntity BatchEvent(
        string namespaceName,
        string type,
        DateTimeOffset now,
        object data) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = namespaceName,
        ClaimName = "*",
        Type = type,
        Actor = "scanner",
        DataJson = JsonColumn.Write(data),
        CreatedAt = now,
    };

    private async Task FinishAsync(
        ScanRunEntity scan,
        int scanned,
        int succeeded,
        int volumeCount,
        List<ScanError> errors,
        CancellationToken ct)
    {
        scan.FinishedAt = time.GetUtcNow();
        scan.NamespacesScanned = scanned;
        scan.NamespacesSucceeded = succeeded;
        scan.VolumeCount = volumeCount;
        scan.ErrorsJson = JsonColumn.Write(errors);
        await db.SaveChangesAsync(ct);
    }

    private static ScanSummary ToSummary(ScanRunEntity scan) => new(
        scan.Id,
        scan.Trigger,
        scan.StartedAt,
        scan.FinishedAt ?? scan.StartedAt,
        scan.NamespacesScanned,
        scan.NamespacesSucceeded,
        scan.VolumeCount,
        JsonColumn.Read<List<ScanError>>(scan.ErrorsJson, []));
}
```

新建 `backend/Infrastructure/Volumes/ScanCoordinator.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class ScanInProgressException() : Exception("上一轮扫描仍在运行");

public sealed class ScanCoordinator(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<ScanSummary> ScanAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new ScanInProgressException();

        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<InventoryScanService>();
            return await service.ScanAsync(trigger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

新建 `backend/Infrastructure/Volumes/InventoryScanner.cs`：

```csharp
using Microsoft.Extensions.Options;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class InventoryScanner(
    ScanCoordinator coordinator,
    IOptions<VolumeManagerOptions> options,
    ILogger<InventoryScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        if (options.Value.ScanOnStartup)
            await RunAsync("startup", stoppingToken);

        using var timer = new PeriodicTimer(options.Value.ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunAsync("scheduled", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(string trigger, CancellationToken ct)
    {
        try
        {
            await coordinator.ScanAsync(trigger, ct);
        }
        catch (ScanInProgressException)
        {
            logger.LogInformation("上一轮扫描仍在运行，跳过 {Trigger} 扫描", trigger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "{Trigger} 扫描失败", trigger);
        }
    }
}
```

- [ ] **Step 4: 运行扫描测试确认通过**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj --filter "FullyQualifiedName~InventoryScanServiceTests"`

Expected: 全部通过。

- [ ] **Step 5: 运行全部后端测试**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add backend/Infrastructure/Volumes/InventoryScanService.cs backend/Infrastructure/Volumes/ScanCoordinator.cs backend/Infrastructure/Volumes/InventoryScanner.cs tests/OpenSandbox.Web.Tests/Volumes/FixedTimeProvider.cs tests/OpenSandbox.Web.Tests/Volumes/InventoryScanServiceTests.cs
git commit -m "feat(volumes): add inventory scanner" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## 验收清单（对应 spec FR-2、FR-5、FR-6、NFR-8）

- [ ] 分类器覆盖缺失/空白/`ephemeral`/`business`/拼错/大写/未知值，未知值按 `Business`。
- [ ] owner 规范化：非法字符替换，超长或首尾非法时省略。
- [ ] `active + scan 成功 + PVC 存在` 保持 active；`scan 失败` 不动。
- [ ] `never-observed` 优先于按 lifecycle 归因。
- [ ] `business` 消失 → `unexpected`；`ephemeral` 消失 → `ephemeral-gone`。
- [ ] 同名新 UID 不迁移归属，只记一次 `reappeared-with-new-uid`。
- [ ] 命名空间 404 → 全部台账 `missing(namespace-deleted)` + 一条批量事件，无逐条高优先级事件。
- [ ] 命名空间失去受管标签 → `unmanaged`，不判消失。
- [ ] 单命名空间 list 失败不影响其他命名空间，且不删除观测、不迁移状态。
- [ ] `pending` 标签在扫描时重试；失败只记事件。
- [ ] 观测表只保留当前存在的卷；台账行永不物理删除。
- [ ] SQLite 测试与 Npgsql 迁移都能构建通过。
