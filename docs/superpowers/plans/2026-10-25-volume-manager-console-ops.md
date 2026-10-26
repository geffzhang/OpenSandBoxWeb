# 卷管理器控制台与运维 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为全局卷管理器提供 CEL 删除保护清单、完整的卷管理控制台页面，以及可执行的部署与运维文档。

**Architecture:** 前端在现有 React SPA 中新增 `src/api/volume.ts` 数据层、五个卷管理页面和「卷管理」菜单组；路由与按钮复用 Plan A 的 `useAuth().can()` 分面角色。后端新增 `deploy/volume-manager/` 下的 RBAC、VAP 与命名空间打标示例；docker-compose 增加 PostgreSQL 17；运维手册覆盖租户接入、扫描、导入与删除保护上线顺序。

**Tech Stack:** React 19.3.1、Vite 8.3.4、Ant Design 5.29.3、TanStack Query 5.104.5、axios 1.20、Vitest 5.0.5、TypeScript ~6.0.3；Kubernetes 1.30+（ValidatingAdmissionPolicy）、PostgreSQL 17、Docker Compose。

**Spec:** `docs/superpowers/specs/2026-10-25-global-volume-manager-design.md`

**Prerequisite:** 先完成 `2026-10-25-console-keycloak-auth.md`（Plan A）与 `2026-10-25-volume-manager-core.md`、`2026-10-25-volume-manager-api.md`（Plan B1/B2）。本计划直接使用 Plan A 的 `useAuth()`、`can()`、`AppLayout`，以及 B2 的 `/api/v1/volumes*`、`/api/v1/volume-tenants*` 契约。

## Global Constraints

- 控制台全部页面与 `/api/v1/volumes*`、`/api/v1/volume-tenants*` 都要求 Keycloak 登录；前端守卫只负责体验，权限以 后端 RBAC 为准。
- 角色只有四个：`sandbox:admin`、`sandbox:viewer`、`volume:admin`、`volume:viewer`；admin 隐含同分面 viewer，分面之间互不隐含。
- **不提供任何删除 PVC 的 API 或 UI 操作**。生命周期只允许 `business` / `ephemeral`。
- 降级为 `ephemeral` 必须填写原因（后端上限 512 字符）；前端同步校验。
- 受管命名空间标签固定为 `opensandbox.io/volume-managed=true`。
- CRD 标签固定为：`opensandbox.io/volume-lifecycle`、`opensandbox.io/volume-tenant`、`opensandbox.io/volume-owner`、`opensandbox.io/volume-contract-version`。
- 删除保护审批组固定为 `opensandbox-volume-approvers`；VAP 先 `Warn + Audit`，观察期结束后才允许 `Deny`。
- RBAC 只授予 `namespaces get/list`、`persistentvolumeclaims get/list/patch`、`pods get/list`；不得出现 `delete`、`create`、`update`。
- 列表筛选、搜索、排序、分页全部由服务端完成；前端把状态同步到 URL query。
- CSV 导入默认 dry-run；只有 dry-run 完成后才允许点「确认写入」；导入永不覆盖其他租户的归属。
- 所有新增前端文件使用 2 空格缩进、单引号、无分号，与现有代码一致。
- 每个提交以 `Co-Authored-By: Claude Code <noreply@anthropic.com>` 结尾。

## File Structure

| 文件 | 职责 |
| --- | --- |
| `deploy/volume-manager/rbac.yaml` | ServiceAccount + ClusterRole + ClusterRoleBinding |
| `deploy/volume-manager/protection-policy.yaml` | VAP + Binding（初始 Warn + Audit） |
| `deploy/volume-manager/namespace-labels.example.yaml` | 受管命名空间打标示例 |
| `deploy/volume-manager/verify-manifests.sh` | 离线清单断言；有集群时追加 server dry-run |
| `deploy/volume-manager/kind-protection-test.sh` | kind 上的删除保护端到端验证 |
| `deploy/volume-manager/keycloak-client.md` | Keycloak client / 角色 / redirect 配置步骤 |
| `frontend/src/api/volume.ts` | 卷管理 HTTP 数据层 |
| `frontend/src/api/types.ts` | 追加卷、租户、报告、导入 DTO 类型 |
| `frontend/src/utils/volumeCsv.ts` | CSV 解析与行级校验 |
| `frontend/src/utils/volumeLabels.ts` | 分类/生命周期中文标签与颜色 |
| `frontend/src/components/volumes/VolumeLifecycleModal.tsx` | 降级/升级弹窗 |
| `frontend/src/components/volumes/TenantFormModal.tsx` | 新建/编辑租户 |
| `frontend/src/components/volumes/TenantKeysModal.tsx` | Key 列表、签发、吊销 |
| `frontend/src/pages/VolumeList.tsx` | `/volumes` 服务端分页列表 |
| `frontend/src/pages/VolumeDetail.tsx` | `/volumes/:namespace/:claimName` 详情 |
| `frontend/src/pages/VolumeTenants.tsx` | `/volume-tenants` 租户与 Key |
| `frontend/src/pages/VolumeReport.tsx` | `/volume-report` 统计与违规 |
| `frontend/src/pages/VolumeImport.tsx` | `/volume-import` CSV 回填 |
| `frontend/src/auth/RequireVolumeRole.tsx` | `volume:viewer` / `volume:admin` 路由守卫 |
| `frontend/src/auth/RoleHome.tsx` | `/` 按角色重定向 |
| `docs/volume-manager-operations.md` | 租户接入、扫描、导入、删除保护与故障排查 |
| `docker-compose.yml` | 新增 PostgreSQL 17 与后端连接串 |
| `.env.example` | Postgres 与卷管理开关 |
| `README.md` | 功能、配置、API 与部署说明 |

---

### Task 1: Kubernetes RBAC、删除保护与清单校验

**Files:**

- Create: `deploy/volume-manager/rbac.yaml`
- Create: `deploy/volume-manager/protection-policy.yaml`
- Create: `deploy/volume-manager/namespace-labels.example.yaml`
- Create: `deploy/volume-manager/verify-manifests.sh`
- Create: `deploy/volume-manager/kind-protection-test.sh`

**Interfaces:**

- Produces: 集群清单与可重复执行的校验脚本，供 Task 6 运维文档引用
- Consumes: 无（纯 YAML/Shell）

- [ ] **Step 1: 先写失败校验脚本**

新建 `deploy/volume-manager/verify-manifests.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "FAIL: $*" >&2; exit 1; }

for file in rbac.yaml protection-policy.yaml namespace-labels.example.yaml; do
  [[ -f "$root/$file" ]] || fail "缺少 $file"
done

if grep -Eq 'verbs:.*\b(delete|create|update)\b' "$root/rbac.yaml"; then
  fail "RBAC 不得授予 delete/create/update"
fi
grep -q 'resources: \["namespaces"\]' "$root/rbac.yaml" || fail "缺少 namespaces 规则"
grep -q 'resources: \["persistentvolumeclaims"\]' "$root/rbac.yaml" || fail "缺少 PVC 规则"
grep -q 'resources: \["pods"\]' "$root/rbac.yaml" || fail "缺少 pods 规则"
grep -q 'name: opensandbox-volume-approvers' "$root/protection-policy.yaml" || fail "缺少审批组"
grep -q 'validationActions: \["Warn", "Audit"\]' "$root/protection-policy.yaml" || fail "初始必须 Warn + Audit"
grep -q "opensandbox.io/volume-managed: \"true\"" "$root/namespace-labels.example.yaml" || fail "缺少受管命名空间标签"

if command -v kubectl >/dev/null 2>&1 && kubectl cluster-info >/dev/null 2>&1; then
  kubectl apply --dry-run=server -f "$root/rbac.yaml" >/dev/null
  kubectl apply --dry-run=server -f "$root/protection-policy.yaml" >/dev/null
  kubectl apply --dry-run=server -f "$root/namespace-labels.example.yaml" >/dev/null
  echo "manifests ok (server dry-run)"
else
  echo "manifests ok (offline; skipped server dry-run)"
fi
```

新建 `deploy/volume-manager/kind-protection-test.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cluster="opensandbox-protection-test"
cleanup() { kind delete cluster --name "$cluster" >/dev/null 2>&1 || true; }
trap cleanup EXIT

command -v kind >/dev/null || { echo "需要 kind" >&2; exit 1; }
command -v kubectl >/dev/null || { echo "需要 kubectl" >&2; exit 1; }

kind create cluster --name "$cluster" --wait 120s
kubectl apply -f "$root/namespace-labels.example.yaml"
kubectl apply -f "$root/protection-policy.yaml"
kubectl patch validatingadmissionpolicybinding opensandbox-protect-business-volumes \
  --type=merge -p '{"spec":{"validationActions":["Deny"]}}'

kubectl -n opensandbox apply -f - <<'YAML'
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: protected
  labels:
    opensandbox.io/volume-lifecycle: business
spec:
  accessModes: ["ReadWriteOnce"]
  resources:
    requests:
      storage: 1Gi
YAML

if kubectl -n opensandbox delete pvc protected --wait=false 2>/dev/null; then
  echo "FAIL: business PVC 删除未被拒绝" >&2
  exit 1
fi
echo "PASS: business PVC 删除被拒绝"

kubectl -n opensandbox label pvc protected opensandbox.io/volume-lifecycle=ephemeral --overwrite
kubectl -n opensandbox delete pvc protected --wait=false
echo "PASS: ephemeral PVC 允许删除"
```

- [ ] **Step 2: 运行校验确认失败**

Run: `bash deploy/volume-manager/verify-manifests.sh`

Expected: `FAIL: 缺少 rbac.yaml`。

- [ ] **Step 3: 写 RBAC 清单**

新建 `deploy/volume-manager/rbac.yaml`：

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: opensandbox-volume-manager
  namespace: opensandbox
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: opensandbox-volume-manager
rules:
  - apiGroups: [""]
    resources: ["namespaces"]
    verbs: ["get", "list"]
  - apiGroups: [""]
    resources: ["persistentvolumeclaims"]
    verbs: ["get", "list", "patch"]
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: opensandbox-volume-manager
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: opensandbox-volume-manager
subjects:
  - kind: ServiceAccount
    name: opensandbox-volume-manager
    namespace: opensandbox
```

- [ ] **Step 4: 写删除保护清单**

新建 `deploy/volume-manager/protection-policy.yaml`：

```yaml
apiVersion: admissionregistration.k8s.io/v1
kind: ValidatingAdmissionPolicy
metadata:
  name: opensandbox-protect-business-volumes
spec:
  failurePolicy: Fail
  matchConstraints:
    resourceRules:
      - apiGroups: [""]
        apiVersions: ["v1"]
        operations: ["DELETE"]
        resources: ["persistentvolumeclaims"]
  variables:
    - name: isProtected
      expression: >-
        has(object.metadata.labels) &&
        'opensandbox.io/volume-lifecycle' in object.metadata.labels &&
        object.metadata.labels['opensandbox.io/volume-lifecycle'] != 'ephemeral'
    - name: isApprover
      expression: >-
        request.userInfo.groups.exists(g, g == 'opensandbox-volume-approvers')
  validations:
    - expression: "!variables.isProtected || variables.isApprover"
      message: >-
        该 PVC 被视为业务持久卷（opensandbox.io/volume-lifecycle 存在且不为 ephemeral）。
        删除需 opensandbox-volume-approvers 组身份，或先由租户通过 VolumeManager API 降级为 ephemeral。
---
apiVersion: admissionregistration.k8s.io/v1
kind: ValidatingAdmissionPolicyBinding
metadata:
  name: opensandbox-protect-business-volumes
spec:
  policyName: opensandbox-protect-business-volumes
  # P2 只观察；P3 将 validationActions 改为 ["Deny"]（见运维手册）。
  validationActions: ["Warn", "Audit"]
  matchResources:
    namespaceSelector:
      matchLabels:
        opensandbox.io/volume-managed: "true"
```

新建 `deploy/volume-manager/namespace-labels.example.yaml`：

```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: opensandbox
  labels:
    opensandbox.io/volume-managed: "true"
```

- [ ] **Step 5: 运行离线校验确认通过**

Run: `bash deploy/volume-manager/verify-manifests.sh`

Expected: `manifests ok (offline; skipped server dry-run)` 或 `manifests ok (server dry-run)`。

- [ ] **Step 6: 运行 kind 端到端验证（有 Docker 时必跑）**

Run: `bash deploy/volume-manager/kind-protection-test.sh`

Expected：脚本输出 `PASS: business PVC 删除被拒绝` 与 `PASS: ephemeral PVC 允许删除`，随后自动删除 kind 集群。若本机没有 Docker/kind，记录未执行原因，留到 Task 6 验收时补跑。

- [ ] **Step 7: 提交**

```bash
git add deploy/volume-manager/rbac.yaml deploy/volume-manager/protection-policy.yaml deploy/volume-manager/namespace-labels.example.yaml deploy/volume-manager/verify-manifests.sh deploy/volume-manager/kind-protection-test.sh
git commit -m "feat(volumes): add kubernetes protection manifests" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 前端卷数据层与 CSV 解析

**Files:**

- Modify: `frontend/src/api/types.ts`
- Create: `frontend/src/api/volume.ts`
- Create: `frontend/src/utils/volumeLabels.ts`
- Create: `frontend/src/utils/volumeCsv.ts`
- Create: `frontend/src/api/__tests__/volume.test.ts`
- Create: `frontend/src/utils/__tests__/volumeCsv.test.ts`
- Create: `frontend/src/utils/__tests__/volumeLabels.test.ts`

**Interfaces:**

- Consumes: `apiClient`（`frontend/src/api/client.ts`）
- Produces: `volumeApi`、`VolumeListParams`、`parseVolumeCsv`、`classificationLabel`

- [ ] **Step 1: 写数据层失败测试**

新建 `frontend/src/api/__tests__/volume.test.ts`：

```ts
import { afterEach, describe, expect, it, vi } from 'vitest'
import { apiClient } from '../client'
import { volumeApi } from '../volume'

describe('volumeApi', () => {
  afterEach(() => vi.restoreAllMocks())

  it('encodes namespace and claim name in detail paths', async () => {
    const get = vi.spyOn(apiClient, 'get').mockResolvedValue({ data: { volume: {} } })

    await volumeApi.get('ns a', 'pvc/1')

    expect(get).toHaveBeenCalledWith('/volumes/ns%20a/pvc%2F1')
  })

  it('allows 120s for imports', async () => {
    const post = vi
      .spyOn(apiClient, 'post')
      .mockResolvedValue({ data: { dryRun: true, results: [] } })
    const request = { dryRun: true, items: [] }

    await volumeApi.import(request)

    expect(post).toHaveBeenCalledWith('/volumes/import', request, { timeout: 120000 })
  })

  it('revokes a tenant key with POST and no body', async () => {
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ data: null })

    await volumeApi.revokeKey('goodcrew', 'key-1')

    expect(post).toHaveBeenCalledWith('/volume-tenants/goodcrew/keys/key-1/revoke')
  })
})
```

新建 `frontend/src/utils/__tests__/volumeCsv.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { parseVolumeCsv } from '../volumeCsv'

const HEADER =
  'tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId'

describe('parseVolumeCsv', () => {
  it('parses quoted fields, a BOM and CRLF', () => {
    const text =
      '﻿' +
      HEADER +
      '\r\n"goodcrew","opensandbox","pvc-1","employee","3f2a","meta","/work, space","business","sb-1"\r\n'

    const result = parseVolumeCsv(text)

    expect(result.errors).toEqual([])
    expect(result.items).toEqual([
      {
        tenant: 'goodcrew',
        namespace: 'opensandbox',
        claimName: 'pvc-1',
        ownerKind: 'employee',
        ownerId: '3f2a',
        role: 'meta',
        mountPath: '/work, space',
        lifecycle: 'business',
        sandboxId: 'sb-1',
      },
    ])
  })

  it('reports a missing required header', () => {
    const result = parseVolumeCsv('tenant,namespace\n"goodcrew","opensandbox"')

    expect(result.items).toEqual([])
    expect(result.errors[0]).toContain('缺少表头')
    expect(result.errors[0]).toContain('claimName')
  })

  it('keeps valid rows and reports invalid lifecycle', () => {
    const text = [
      HEADER,
      'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,/work,business,sb-1',
      'goodcrew,opensandbox,pvc-2,employee,aaaa,meta,/work,forever,',
    ].join('\n')

    const result = parseVolumeCsv(text)

    expect(result.items.map((item) => item.claimName)).toEqual(['pvc-1'])
    expect(result.errors).toEqual(['第 3 行: lifecycle 必须是 business 或 ephemeral'])
  })

  it('handles newlines inside quoted fields', () => {
    const text = [
      HEADER,
      'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,"/line1\nline2",business,',
    ].join('\n')

    const result = parseVolumeCsv(text)

    expect(result.errors).toEqual([])
    expect(result.items[0].mountPath).toBe('/line1\nline2')
  })

  it('reports an unclosed quote', () => {
    const result = parseVolumeCsv(HEADER + '\n"goodcrew,opensandbox')

    expect(result.items).toEqual([])
    expect(result.errors).toEqual(['CSV 含未闭合的引号'])
  })
})
```

新建 `frontend/src/utils/__tests__/volumeLabels.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { classificationLabel, lifecycleLabel } from '../volumeLabels'

describe('volumeLabels', () => {
  it('maps known classifications', () => {
    expect(classificationLabel('unregistered')).toBe('未登记')
    expect(classificationLabel('missing')).toBe('缺失')
  })

  it('falls back to the raw value', () => {
    expect(classificationLabel('future-state')).toBe('future-state')
    expect(lifecycleLabel('other')).toBe('other')
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/api/__tests__/volume.test.ts src/utils/__tests__/volumeCsv.test.ts src/utils/__tests__/volumeLabels.test.ts`

Expected: 编译失败，提示找不到 `../volume` / `../volumeCsv` / `../volumeLabels`。

- [ ] **Step 3: 追加 DTO 类型**

在 `frontend/src/api/types.ts` 末尾追加：

```ts
export interface VolumeListItem {
  id?: string | null
  namespace: string
  claimName: string
  tenant?: string | null
  tenantName?: string | null
  ownerKind?: string | null
  ownerId?: string | null
  role?: string | null
  mountPath?: string | null
  lifecycle?: string | null
  state?: string | null
  stateReason?: string | null
  classification: string
  labelState?: string | null
  violations: string[]
  registeredUid?: string | null
  observedUid?: string | null
  registeredAt?: string | null
  lastSeenAt?: string | null
  mounted?: boolean | null
  storageClass?: string | null
  capacity?: string | null
  createdAt?: string | null
}

export interface VolumeListResponse {
  items: VolumeListItem[]
  pagination: PaginationInfo
}

export interface ClusterVolume {
  namespace: string
  claimName: string
  uid: string
  phase: string
  storageClass?: string | null
  requestedStorage?: string | null
  capacity?: string | null
  createdAt: string
  deletionTimestamp?: string | null
  mounted: boolean
  mountedBy: string[]
  labels: Record<string, string>
  lifecycleLabel?: string | null
  tenantLabel?: string | null
  ownerLabel?: string | null
  contractVersionLabel?: string | null
  firstSeenAt: string
  lastSeenAt: string
}

export interface VolumeEvent {
  id: string
  volumeId?: string | null
  namespace: string
  claimName: string
  tenantId?: string | null
  type: string
  actor: string
  reason?: string | null
  dataJson: string
  createdAt: string
}

export interface VolumeDetail {
  volume: VolumeListItem
  observed?: ClusterVolume | null
  events: VolumeEvent[]
}

export interface VolumeLifecycleRequest {
  lifecycle: 'business' | 'ephemeral'
  reason?: string
}

export interface VolumeLifecycleResponse {
  status: string
  labelState: string
}

export interface VolumeReportCounts {
  observed: number
  registered: number
  unlabeled: number
  unregistered: number
  drift: number
  missing: number
  reappeared: number
  unmanaged: number
}

export interface VolumeCoverage {
  observed: number
  labeled: number
  registered: number
  labeledPercent: number
  registeredPercent: number
}

export interface VolumeDistributionItem {
  key: string
  count: number
}

export interface VolumeViolation {
  type: string
  namespace: string
  claimName: string
  message: string
}

export interface ScanError {
  namespace: string
  message: string
}

export interface ScanRun {
  id: string
  trigger: string
  startedAt: string
  finishedAt?: string | null
  namespacesScanned: number
  namespacesSucceeded: number
  volumeCount: number
  errors: ScanError[]
}

export interface VolumeScanResponse {
  scanId: string
  trigger: string
  startedAt: string
  finishedAt: string
  namespacesScanned: number
  namespacesSucceeded: number
  volumeCount: number
  errors: ScanError[]
}

export interface VolumeReport {
  lastScan?: ScanRun | null
  counts: VolumeReportCounts
  coverage: VolumeCoverage
  byTenant: VolumeDistributionItem[]
  byNamespace: VolumeDistributionItem[]
  violations: VolumeViolation[]
  truncated: boolean
}

export interface VolumeImportItem {
  tenant: string
  namespace: string
  claimName: string
  ownerKind: string
  ownerId: string
  role?: string | null
  mountPath?: string | null
  lifecycle: string
  sandboxId?: string | null
}

export interface VolumeImportRequest {
  dryRun?: boolean
  items: VolumeImportItem[]
}

export interface VolumeImportResult {
  namespace: string
  claimName: string
  status: string
  message?: string | null
}

export interface VolumeImportResponse {
  dryRun: boolean
  results: VolumeImportResult[]
}

export interface VolumeTenant {
  id: string
  displayName: string
  status: string
  namespaces: string[]
  volumeCount: number
  protectedCount: number
  ephemeralCount: number
  missingCount: number
  keyCount: number
  createdAt: string
}

export interface TenantListResponse {
  items: VolumeTenant[]
}

export interface CreateTenantRequest {
  id: string
  displayName: string
  namespaces: string[]
}

export interface UpdateTenantRequest {
  displayName?: string | null
  status?: string | null
  namespaces?: string[] | null
}

export interface TenantKey {
  id: string
  name: string
  prefix: string
  createdAt: string
  revokedAt?: string | null
  lastUsedAt?: string | null
}

export interface TenantKeyListResponse {
  items: TenantKey[]
}

export interface CreateTenantKeyRequest {
  name?: string | null
}

export interface CreateTenantKeyResponse {
  id: string
  key: string
  prefix: string
}
```

- [ ] **Step 4: 实现数据层与标签工具**

新建 `frontend/src/api/volume.ts`：

```ts
import { apiClient } from './client'
import type {
  CreateTenantKeyRequest,
  CreateTenantKeyResponse,
  CreateTenantRequest,
  TenantKeyListResponse,
  TenantListResponse,
  UpdateTenantRequest,
  VolumeDetail,
  VolumeImportRequest,
  VolumeImportResponse,
  VolumeLifecycleRequest,
  VolumeLifecycleResponse,
  VolumeListResponse,
  VolumeReport,
  VolumeScanResponse,
  VolumeTenant,
} from './types'

export interface VolumeListParams {
  page?: number
  pageSize?: number
  namespace?: string
  tenant?: string
  classification?: string
  lifecycle?: string
  mounted?: boolean
  q?: string
  sort?: string
  order?: 'asc' | 'desc'
}

const segment = (value: string) => encodeURIComponent(value)

export const volumeApi = {
  list: (params: VolumeListParams) =>
    apiClient.get<VolumeListResponse>('/volumes', { params }).then((response) => response.data),

  get: (namespace: string, claimName: string) =>
    apiClient
      .get<VolumeDetail>(`/volumes/${segment(namespace)}/${segment(claimName)}`)
      .then((response) => response.data),

  changeLifecycle: (
    namespace: string,
    claimName: string,
    request: VolumeLifecycleRequest,
  ) =>
    apiClient
      .post<VolumeLifecycleResponse>(
        `/volumes/${segment(namespace)}/${segment(claimName)}/lifecycle`,
        request,
      )
      .then((response) => response.data),

  scan: () =>
    apiClient
      .post<VolumeScanResponse>('/volumes/scan')
      .then((response) => response.data),

  report: () =>
    apiClient.get<VolumeReport>('/volumes/report').then((response) => response.data),

  import: (request: VolumeImportRequest) =>
    apiClient
      .post<VolumeImportResponse>('/volumes/import', request, { timeout: 120000 })
      .then((response) => response.data),

  tenants: () =>
    apiClient
      .get<TenantListResponse>('/volume-tenants')
      .then((response) => response.data.items),

  createTenant: (request: CreateTenantRequest) =>
    apiClient
      .post<VolumeTenant>('/volume-tenants', request)
      .then((response) => response.data),

  updateTenant: (tenantId: string, request: UpdateTenantRequest) =>
    apiClient
      .patch<VolumeTenant>(`/volume-tenants/${segment(tenantId)}`, request)
      .then((response) => response.data),

  keys: (tenantId: string) =>
    apiClient
      .get<TenantKeyListResponse>(`/volume-tenants/${segment(tenantId)}/keys`)
      .then((response) => response.data.items),

  createKey: (tenantId: string, request: CreateTenantKeyRequest) =>
    apiClient
      .post<CreateTenantKeyResponse>(
        `/volume-tenants/${segment(tenantId)}/keys`,
        request,
      )
      .then((response) => response.data),

  revokeKey: (tenantId: string, keyId: string) =>
    apiClient
      .post(`/volume-tenants/${segment(tenantId)}/keys/${segment(keyId)}/revoke`)
      .then(() => undefined),
}
```

新建 `frontend/src/utils/volumeLabels.ts`：

```ts
export const CLASSIFICATION_LABELS: Record<string, string> = {
  registered: '已登记',
  unlabeled: '未标注',
  unregistered: '未登记',
  drift: '标签漂移',
  missing: '缺失',
  reappeared: '新 UID 重现',
  unmanaged: '命名空间失管',
}

export const CLASSIFICATION_COLORS: Record<string, string> = {
  registered: 'green',
  unlabeled: 'red',
  unregistered: 'orange',
  drift: 'volcano',
  missing: 'magenta',
  reappeared: 'purple',
  unmanaged: 'default',
}

export const LIFECYCLE_LABELS: Record<string, string> = {
  business: '业务持久',
  ephemeral: '临时',
}

export const LIFECYCLE_COLORS: Record<string, string> = {
  business: 'blue',
  ephemeral: 'default',
}

export function classificationLabel(value: string): string {
  return CLASSIFICATION_LABELS[value] ?? value
}

export function lifecycleLabel(value: string): string {
  return LIFECYCLE_LABELS[value] ?? value
}
```

- [ ] **Step 5: 实现 CSV 解析**

新建 `frontend/src/utils/volumeCsv.ts`：

```ts
import type { VolumeImportItem } from '@/api/types'

export interface VolumeCsvParseResult {
  items: VolumeImportItem[]
  errors: string[]
}

type Column = keyof VolumeImportItem

const HEADER_ALIASES: Record<string, Column> = {
  tenant: 'tenant',
  namespace: 'namespace',
  claimname: 'claimName',
  ownerkind: 'ownerKind',
  ownerid: 'ownerId',
  role: 'role',
  mountpath: 'mountPath',
  lifecycle: 'lifecycle',
  sandboxid: 'sandboxId',
}

const REQUIRED: Column[] = [
  'tenant',
  'namespace',
  'claimName',
  'ownerKind',
  'ownerId',
  'lifecycle',
]

const normalizeHeader = (value: string) =>
  value.trim().replace(/[\s_-]/g, '').toLowerCase()

function parseRecords(text: string): string[][] {
  const source = text.replace(/^﻿/, '')
  const records: string[][] = []
  let record: string[] = []
  let field = ''
  let quoted = false

  for (let index = 0; index < source.length; index += 1) {
    const char = source[index]
    if (quoted) {
      if (char === '"') {
        if (source[index + 1] === '"') {
          field += '"'
          index += 1
        } else {
          quoted = false
        }
      } else {
        field += char
      }
      continue
    }

    if (char === '"') {
      quoted = true
    } else if (char === ',') {
      record.push(field)
      field = ''
    } else if (char === '\r') {
      continue
    } else if (char === '\n') {
      record.push(field)
      records.push(record)
      record = []
      field = ''
    } else {
      field += char
    }
  }

  if (quoted) throw new Error('CSV 含未闭合的引号')
  if (field.length > 0 || record.length > 0) {
    record.push(field)
    records.push(record)
  }

  return records.filter(
    (values) => values.length > 1 || values[0]?.trim().length > 0,
  )
}

export function parseVolumeCsv(text: string): VolumeCsvParseResult {
  if (!text.trim()) return { items: [], errors: ['CSV 为空'] }

  let records: string[][]
  try {
    records = parseRecords(text)
  } catch (error) {
    return {
      items: [],
      errors: [error instanceof Error ? error.message : 'CSV 解析失败'],
    }
  }

  if (records.length === 0) return { items: [], errors: ['CSV 为空'] }

  const columns = records[0].map((header) => HEADER_ALIASES[normalizeHeader(header)])
  const missing = REQUIRED.filter((column) => !columns.includes(column))
  if (missing.length > 0) {
    return { items: [], errors: [`缺少表头: ${missing.join(', ')}`] }
  }

  const items: VolumeImportItem[] = []
  const errors: string[] = []

  records.slice(1).forEach((values, rowIndex) => {
    const line = rowIndex + 2
    if (values.length !== records[0].length) {
      errors.push(`第 ${line} 行: 列数与表头不一致`)
      return
    }

    const row: Partial<Record<Column, string>> = {}
    columns.forEach((column, columnIndex) => {
      if (column) row[column] = values[columnIndex].trim()
    })

    const absent = REQUIRED.filter((column) => !row[column])
    if (absent.length > 0) {
      errors.push(`第 ${line} 行: 缺少字段 ${absent.join(', ')}`)
      return
    }

    if (row.lifecycle !== 'business' && row.lifecycle !== 'ephemeral') {
      errors.push(`第 ${line} 行: lifecycle 必须是 business 或 ephemeral`)
      return
    }

    items.push({
      tenant: row.tenant!,
      namespace: row.namespace!,
      claimName: row.claimName!,
      ownerKind: row.ownerKind!,
      ownerId: row.ownerId!,
      role: row.role || undefined,
      mountPath: row.mountPath || undefined,
      lifecycle: row.lifecycle!,
      sandboxId: row.sandboxId || undefined,
    })
  })

  return { items, errors }
}
```

- [ ] **Step 6: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/api/__tests__/volume.test.ts src/utils/__tests__/volumeCsv.test.ts src/utils/__tests__/volumeLabels.test.ts`

Expected: 全部通过。

- [ ] **Step 7: 类型检查**

Run（在 `frontend/`）: `npx tsc -b`

Expected: 通过。

- [ ] **Step 8: 提交**

```bash
git add frontend/src/api/types.ts frontend/src/api/volume.ts frontend/src/utils/volumeLabels.ts frontend/src/utils/volumeCsv.ts frontend/src/api/__tests__/volume.test.ts frontend/src/utils/__tests__/volumeCsv.test.ts frontend/src/utils/__tests__/volumeLabels.test.ts
git commit -m "feat(volumes): add console data layer and csv parsing" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 卷列表与详情页

**Files:**

- Create: `frontend/src/components/volumes/VolumeLifecycleModal.tsx`
- Create: `frontend/src/pages/VolumeList.tsx`
- Create: `frontend/src/pages/VolumeDetail.tsx`
- Create: `frontend/src/pages/__tests__/VolumeList.test.tsx`

**Interfaces:**

- Consumes: Task 2 `volumeApi`、类型、标签工具；Plan A `useAuth`
- Produces: `/volumes` 与 `/volumes/:namespace/:claimName` 页面组件

- [ ] **Step 1: 写列表页失败测试**

新建 `frontend/src/pages/__tests__/VolumeList.test.tsx`：

```tsx
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeListResponse, VolumeReport } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeList from '../VolumeList'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { username: 'alice', roles: ['volume:viewer'] },
    can: (role: string) => role === 'volume:viewer',
  }),
}))

vi.mock('@/api/volume', () => ({
  volumeApi: {
    list: vi.fn(),
    report: vi.fn(),
    scan: vi.fn(),
    changeLifecycle: vi.fn(),
  },
}))

const listResponse: VolumeListResponse = {
  items: [
    {
      namespace: 'opensandbox',
      claimName: 'pvc-1',
      tenant: 'goodcrew',
      tenantName: 'GoodCrew',
      classification: 'registered',
      lifecycle: 'business',
      state: 'active',
      mounted: true,
      violations: [],
    },
  ],
  pagination: {
    page: 1,
    pageSize: 20,
    totalItems: 1,
    totalPages: 1,
    hasNextPage: false,
  },
}

const report: VolumeReport = {
  lastScan: null,
  counts: {
    observed: 1,
    registered: 1,
    unlabeled: 0,
    unregistered: 0,
    drift: 0,
    missing: 0,
    reappeared: 0,
    unmanaged: 0,
  },
  coverage: {
    observed: 1,
    labeled: 1,
    registered: 1,
    labeledPercent: 100,
    registeredPercent: 100,
  },
  byTenant: [],
  byNamespace: [],
  violations: [],
  truncated: false,
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={['/volumes']}>
            <Routes>
              <Route path="/volumes" element={<VolumeList />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeList', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.list).mockResolvedValue(listResponse)
    vi.mocked(volumeApi.report).mockResolvedValue(report)
  })

  it('renders server results and hides admin actions from viewers', async () => {
    renderPage()

    expect(await screen.findByText('pvc-1')).toBeInTheDocument()
    expect(screen.getByText('已登记')).toBeInTheDocument()
    expect(screen.getByText('GoodCrew')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /立即扫描/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /降级/ })).not.toBeInTheDocument()
  })

  it('passes the URL filters to the API', async () => {
    renderPage()

    await screen.findByText('pvc-1')
    expect(volumeApi.list).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 20,
        sort: 'namespace',
        order: 'asc',
      }),
    )
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/pages/__tests__/VolumeList.test.tsx`

Expected: 编译失败，提示找不到 `../VolumeList`。

- [ ] **Step 3: 实现生命周期弹窗**

新建 `frontend/src/components/volumes/VolumeLifecycleModal.tsx`：

```tsx
import { useEffect, useState } from 'react'
import { Alert, App, Descriptions, Input, Modal, Select, Space, Tag, Typography } from 'antd'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeListItem } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { lifecycleLabel } from '@/utils/volumeLabels'

interface VolumeLifecycleModalProps {
  volume: VolumeListItem | null
  open: boolean
  onClose: () => void
  onSubmitted: () => void
}

export default function VolumeLifecycleModal({
  volume,
  open,
  onClose,
  onSubmitted,
}: VolumeLifecycleModalProps) {
  const { message } = App.useApp()
  const [target, setTarget] = useState<'business' | 'ephemeral'>('ephemeral')
  const [reason, setReason] = useState('')

  useEffect(() => {
    if (!open || !volume) return
    setTarget(volume.lifecycle === 'business' ? 'ephemeral' : 'business')
    setReason('')
  }, [open, volume])

  const mutation = useMutation({
    mutationFn: () => {
      if (!volume) throw new Error('未选择卷')
      return volumeApi.changeLifecycle(volume.namespace, volume.claimName, {
        lifecycle: target,
        reason: target === 'ephemeral' ? reason.trim() : undefined,
      })
    },
    onSuccess: () => {
      message.success('生命周期已更新')
      onSubmitted()
      onClose()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const reasonRequired = target === 'ephemeral'

  return (
    <Modal
      open={open}
      title="变更卷生命周期"
      okText="提交"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      okButtonProps={{ disabled: reasonRequired && reason.trim().length === 0 }}
      onCancel={onClose}
      onOk={() => mutation.mutate()}
    >
      {volume && (
        <>
          <Descriptions column={1} size="small" style={{ marginBottom: 16 }}>
            <Descriptions.Item label="卷">
              {volume.namespace}/{volume.claimName}
            </Descriptions.Item>
            <Descriptions.Item label="当前生命周期">
              <Tag>{lifecycleLabel(volume.lifecycle ?? '-')}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label="租户">
              {volume.tenantName ?? volume.tenant ?? '-'}
            </Descriptions.Item>
          </Descriptions>

          {reasonRequired && (
            <Alert
              type="warning"
              showIcon
              message="降级为临时卷后，Kubernetes 删除保护将不再拦截该 PVC。"
              style={{ marginBottom: 16 }}
            />
          )}

          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <div>
              <Typography.Text>目标生命周期</Typography.Text>
              <Select
                value={target}
                onChange={(value) => setTarget(value as 'business' | 'ephemeral')}
                style={{ width: '100%', marginTop: 8 }}
                options={[
                  { value: 'business', label: '业务持久' },
                  { value: 'ephemeral', label: '临时' },
                ]}
              />
            </div>
            <div>
              <Typography.Text>
                原因{reasonRequired ? '（必填）' : '（可选）'}
              </Typography.Text>
              <Input.TextArea
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                rows={3}
                maxLength={512}
                showCount
                placeholder="例如：业务确认该员工工作区不再需要保留"
                style={{ marginTop: 8 }}
              />
            </div>
          </Space>
        </>
      )}
    </Modal>
  )
}
```

- [ ] **Step 4: 实现卷列表页**

新建 `frontend/src/pages/VolumeList.tsx`：

```tsx
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  Alert,
  App,
  Button,
  Card,
  Input,
  Select,
  Space,
  Table,
  Tag,
  Typography,
} from 'antd'
import type { TableProps } from 'antd'
import { ReloadOutlined, ScanOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeListItem } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import VolumeLifecycleModal from '@/components/volumes/VolumeLifecycleModal'
import { formatTime } from '@/utils/format'
import {
  CLASSIFICATION_COLORS,
  LIFECYCLE_COLORS,
  classificationLabel,
  lifecycleLabel,
} from '@/utils/volumeLabels'

const DEFAULT_PAGE_SIZE = 20

const CLASSIFICATION_OPTIONS = Object.keys(CLASSIFICATION_COLORS).map((value) => ({
  value,
  label: classificationLabel(value),
}))

const LIFECYCLE_OPTIONS = [
  { value: 'business', label: lifecycleLabel('business') },
  { value: 'ephemeral', label: lifecycleLabel('ephemeral') },
]

function sortParam(field: unknown): string {
  switch (field) {
    case 'claimName':
      return 'claimName'
    case 'createdAt':
      return 'createdAt'
    case 'lastSeenAt':
      return 'lastSeenAt'
    case 'tenantName':
      return 'tenant'
    default:
      return 'namespace'
  }
}

export default function VolumeList() {
  const { message } = App.useApp()
  const { can } = useAuth()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const [selected, setSelected] = useState<VolumeListItem | null>(null)

  const page = Math.max(1, Number(params.get('page') ?? '1') || 1)
  const pageSize = Math.max(
    1,
    Number(params.get('pageSize') ?? String(DEFAULT_PAGE_SIZE)) || DEFAULT_PAGE_SIZE,
  )
  const namespace = params.get('namespace') ?? undefined
  const tenant = params.get('tenant') ?? undefined
  const classification = params.get('classification') ?? undefined
  const lifecycle = params.get('lifecycle') ?? undefined
  const q = params.get('q') ?? undefined
  const sort = params.get('sort') ?? 'namespace'
  const order: 'asc' | 'desc' = params.get('order') === 'desc' ? 'desc' : 'asc'
  const mountedParam = params.get('mounted')
  const mounted =
    mountedParam === 'true' ? true : mountedParam === 'false' ? false : undefined

  const updateParams = (patch: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params)
    Object.entries(patch).forEach(([key, value]) => {
      if (value === undefined || value === '') next.delete(key)
      else next.set(key, value)
    })
    setParams(next, { replace: true })
  }

  const listQuery = useQuery({
    queryKey: ['volumes', 'list', params.toString()],
    queryFn: () =>
      volumeApi.list({
        page,
        pageSize,
        namespace,
        tenant,
        classification,
        lifecycle,
        mounted,
        q,
        sort,
        order,
      }),
  })

  const reportQuery = useQuery({
    queryKey: ['volumes', 'report'],
    queryFn: volumeApi.report,
    staleTime: 30_000,
  })

  const scanMutation = useMutation({
    mutationFn: volumeApi.scan,
    onSuccess: (result) => {
      message.success(`扫描完成，共 ${result.volumeCount} 个卷`)
      queryClient.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const columns: TableProps<VolumeListItem>['columns'] = [
    {
      title: '命名空间',
      dataIndex: 'namespace',
      key: 'namespace',
      width: 160,
      sorter: true,
      sortOrder: sort === 'namespace' ? (order === 'asc' ? 'ascend' : 'descend') : null,
    },
    {
      title: 'PVC',
      dataIndex: 'claimName',
      key: 'claimName',
      width: 200,
      sorter: true,
      sortOrder: sort === 'claimName' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (claimName: string, record) => (
        <Link
          to={`/volumes/${encodeURIComponent(record.namespace)}/${encodeURIComponent(claimName)}`}
        >
          {claimName}
        </Link>
      ),
    },
    {
      title: '租户',
      dataIndex: 'tenantName',
      key: 'tenantName',
      width: 140,
      sorter: true,
      sortOrder: sort === 'tenant' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (_value, record) => record.tenantName ?? record.tenant ?? '-',
    },
    {
      title: '分类',
      dataIndex: 'classification',
      key: 'classification',
      width: 130,
      render: (value: string) => (
        <Tag color={CLASSIFICATION_COLORS[value] ?? 'default'}>
          {classificationLabel(value)}
        </Tag>
      ),
    },
    {
      title: '生命周期',
      dataIndex: 'lifecycle',
      key: 'lifecycle',
      width: 110,
      render: (value: string | null | undefined) =>
        value ? (
          <Tag color={LIFECYCLE_COLORS[value] ?? 'default'}>{lifecycleLabel(value)}</Tag>
        ) : (
          '-'
        ),
    },
    {
      title: '状态',
      dataIndex: 'state',
      key: 'state',
      width: 130,
      render: (value: string | null | undefined, record) =>
        value ? `${value}${record.stateReason ? ` (${record.stateReason})` : ''}` : '-',
    },
    {
      title: '挂载',
      dataIndex: 'mounted',
      key: 'mounted',
      width: 80,
      render: (value: boolean | null | undefined) =>
        value === null || value === undefined ? '-' : value ? '是' : '否',
    },
    {
      title: 'Owner',
      key: 'owner',
      width: 160,
      render: (_, record) =>
        record.ownerKind ? `${record.ownerKind}/${record.ownerId ?? '-'}` : '-',
    },
    {
      title: '容量',
      dataIndex: 'capacity',
      key: 'capacity',
      width: 100,
      render: (value: string | null | undefined) => value ?? '-',
    },
    {
      title: '最后观测',
      dataIndex: 'lastSeenAt',
      key: 'lastSeenAt',
      width: 170,
      sorter: true,
      sortOrder: sort === 'lastSeenAt' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null | undefined) => formatTime(value),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      sorter: true,
      sortOrder: sort === 'createdAt' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null | undefined) => formatTime(value),
    },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      fixed: 'right',
      render: (_, record) =>
        can('volume:admin') && record.id ? (
          <Button type="link" onClick={() => setSelected(record)}>
            {record.lifecycle === 'business' ? '降级' : '升级'}
          </Button>
        ) : (
          '-'
        ),
    },
  ]

  const handleTableChange: TableProps<VolumeListItem>['onChange'] = (
    pagination,
    _filters,
    sorter,
  ) => {
    const single = Array.isArray(sorter) ? sorter[0] : sorter
    updateParams({
      page: String(pagination.current ?? 1),
      pageSize: String(pagination.pageSize ?? DEFAULT_PAGE_SIZE),
      sort: single?.order ? sortParam(single.field) : undefined,
      order:
        single?.order === 'ascend'
          ? 'asc'
          : single?.order === 'descend'
            ? 'desc'
            : undefined,
    })
  }

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card>
        <Space wrap size={24}>
          <Typography.Text>
            最后扫描：{formatTime(reportQuery.data?.lastScan?.finishedAt)}
          </Typography.Text>
          <Typography.Text>
            标签覆盖率：{reportQuery.data?.coverage.labeledPercent ?? 0}%
          </Typography.Text>
          <Typography.Text>
            登记覆盖率：{reportQuery.data?.coverage.registeredPercent ?? 0}%
          </Typography.Text>
          <Button
            icon={<ReloadOutlined />}
            onClick={() => {
              listQuery.refetch()
              reportQuery.refetch()
            }}
          >
            刷新
          </Button>
          {can('volume:admin') && (
            <Button
              type="primary"
              icon={<ScanOutlined />}
              loading={scanMutation.isPending}
              onClick={() => scanMutation.mutate()}
            >
              立即扫描
            </Button>
          )}
        </Space>
      </Card>

      <Card>
        <Space wrap style={{ marginBottom: 16 }}>
          <Input
            key={`namespace-${namespace ?? ''}`}
            defaultValue={namespace}
            placeholder="命名空间"
            allowClear
            style={{ width: 160 }}
            onPressEnter={(event) =>
              updateParams({
                namespace: event.currentTarget.value.trim() || undefined,
                page: '1',
              })
            }
            onChange={(event) => {
              if (!event.target.value)
                updateParams({ namespace: undefined, page: '1' })
            }}
          />
          <Input
            key={`tenant-${tenant ?? ''}`}
            defaultValue={tenant}
            placeholder="租户 ID"
            allowClear
            style={{ width: 160 }}
            onPressEnter={(event) =>
              updateParams({
                tenant: event.currentTarget.value.trim() || undefined,
                page: '1',
              })
            }
            onChange={(event) => {
              if (!event.target.value) updateParams({ tenant: undefined, page: '1' })
            }}
          />
          <Select
            allowClear
            placeholder="分类"
            style={{ width: 150 }}
            value={classification}
            options={CLASSIFICATION_OPTIONS}
            onChange={(value) =>
              updateParams({ classification: value || undefined, page: '1' })
            }
          />
          <Select
            allowClear
            placeholder="生命周期"
            style={{ width: 140 }}
            value={lifecycle}
            options={LIFECYCLE_OPTIONS}
            onChange={(value) =>
              updateParams({ lifecycle: value || undefined, page: '1' })
            }
          />
          <Select
            allowClear
            placeholder="挂载"
            style={{ width: 120 }}
            value={mountedParam ?? undefined}
            options={[
              { value: 'true', label: '已挂载' },
              { value: 'false', label: '未挂载' },
            ]}
            onChange={(value) =>
              updateParams({ mounted: value || undefined, page: '1' })
            }
          />
          <Input.Search
            key={`q-${q ?? ''}`}
            defaultValue={q}
            allowClear
            placeholder="搜索 PVC / owner / 租户"
            style={{ width: 260 }}
            onSearch={(value) =>
              updateParams({ q: value.trim() || undefined, page: '1' })
            }
          />
          <Button onClick={() => setParams(new URLSearchParams(), { replace: true })}>
            重置
          </Button>
        </Space>

        {listQuery.isError && (
          <Alert
            type="error"
            showIcon
            message={extractErrorMessage(listQuery.error)}
            style={{ marginBottom: 16 }}
          />
        )}

        <Table<VolumeListItem>
          rowKey={(record) => `${record.namespace}/${record.claimName}`}
          loading={listQuery.isLoading || listQuery.isFetching}
          columns={columns}
          dataSource={listQuery.data?.items ?? []}
          scroll={{ x: 1500 }}
          pagination={{
            current: page,
            pageSize,
            total: listQuery.data?.pagination.totalItems ?? 0,
            showSizeChanger: true,
            showTotal: (total) => `共 ${total} 个卷`,
          }}
          onChange={handleTableChange}
        />
      </Card>

      <VolumeLifecycleModal
        volume={selected}
        open={selected !== null}
        onClose={() => setSelected(null)}
        onSubmitted={() => {
          listQuery.refetch()
          reportQuery.refetch()
        }}
      />
    </Space>
  )
}
```

- [ ] **Step 5: 实现卷详情页**

新建 `frontend/src/pages/VolumeDetail.tsx`：

```tsx
import { Link, useParams } from 'react-router-dom'
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Result,
  Space,
  Spin,
  Tag,
  Timeline,
  Typography,
} from 'antd'
import { ArrowLeftOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import { volumeApi } from '@/api/volume'
import { formatTime } from '@/utils/format'
import {
  CLASSIFICATION_COLORS,
  LIFECYCLE_COLORS,
  classificationLabel,
  lifecycleLabel,
} from '@/utils/volumeLabels'

export default function VolumeDetail() {
  const { namespace = '', claimName = '' } = useParams()
  const query = useQuery({
    queryKey: ['volumes', 'detail', namespace, claimName],
    queryFn: () => volumeApi.get(namespace, claimName),
    retry: false,
  })

  if (query.isLoading) return <Spin />
  if (query.isError) {
    return (
      <Result
        status="404"
        title="卷不存在或无权访问"
        subTitle={extractErrorMessage(query.error)}
        extra={
          <Link to="/volumes">
            <Button icon={<ArrowLeftOutlined />}>返回卷列表</Button>
          </Link>
        }
      />
    )
  }

  const detail = query.data!
  const volume = detail.volume

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space>
        <Link to="/volumes">
          <Button icon={<ArrowLeftOutlined />}>返回</Button>
        </Link>
        <Typography.Title level={4} style={{ margin: 0 }}>
          {volume.namespace}/{volume.claimName}
        </Typography.Title>
        <Tag color={CLASSIFICATION_COLORS[volume.classification] ?? 'default'}>
          {classificationLabel(volume.classification)}
        </Tag>
        {volume.lifecycle && (
          <Tag color={LIFECYCLE_COLORS[volume.lifecycle] ?? 'default'}>
            {lifecycleLabel(volume.lifecycle)}
          </Tag>
        )}
      </Space>

      {volume.violations.length > 0 && (
        <Alert
          type="warning"
          showIcon
          message="违规"
          description={volume.violations.join('、')}
        />
      )}

      <Card title="台账">
        <Descriptions column={2} size="small">
          <Descriptions.Item label="租户">
            {volume.tenantName ?? volume.tenant ?? '-'}
          </Descriptions.Item>
          <Descriptions.Item label="Owner">
            {volume.ownerKind ? `${volume.ownerKind}/${volume.ownerId ?? '-'}` : '-'}
          </Descriptions.Item>
          <Descriptions.Item label="角色">{volume.role ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="挂载路径">{volume.mountPath ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="状态">
            {volume.state ?? '-'}
            {volume.stateReason ? ` (${volume.stateReason})` : ''}
          </Descriptions.Item>
          <Descriptions.Item label="标签状态">{volume.labelState ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="登记 UID">{volume.registeredUid ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="观测 UID">{volume.observedUid ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="登记时间">
            {formatTime(volume.registeredAt)}
          </Descriptions.Item>
          <Descriptions.Item label="最后观测">
            {formatTime(volume.lastSeenAt)}
          </Descriptions.Item>
        </Descriptions>
      </Card>

      <Card title="集群观测">
        {detail.observed ? (
          <Descriptions column={2} size="small">
            <Descriptions.Item label="Phase">{detail.observed.phase}</Descriptions.Item>
            <Descriptions.Item label="StorageClass">
              {detail.observed.storageClass ?? '-'}
            </Descriptions.Item>
            <Descriptions.Item label="容量">
              {detail.observed.capacity ?? detail.observed.requestedStorage ?? '-'}
            </Descriptions.Item>
            <Descriptions.Item label="挂载">
              {detail.observed.mounted ? '是' : '否'}
            </Descriptions.Item>
            <Descriptions.Item label="挂载 Pod" span={2}>
              {detail.observed.mountedBy.length > 0 ? (
                <Space wrap>
                  {detail.observed.mountedBy.map((pod) => (
                    <Tag key={pod}>{pod}</Tag>
                  ))}
                </Space>
              ) : (
                '-'
              )}
            </Descriptions.Item>
            <Descriptions.Item label="K8s 创建时间">
              {formatTime(detail.observed.createdAt)}
            </Descriptions.Item>
            <Descriptions.Item label="删除时间戳">
              {formatTime(detail.observed.deletionTimestamp)}
            </Descriptions.Item>
            <Descriptions.Item label="原始标签" span={2}>
              <Space wrap>
                {Object.entries(detail.observed.labels).map(([key, value]) => (
                  <Tag key={key}>
                    {key}={value}
                  </Tag>
                ))}
              </Space>
            </Descriptions.Item>
          </Descriptions>
        ) : (
          <Typography.Text type="secondary">
            当前未观测到该 PVC（可能已被删除或命名空间扫描失败）。
          </Typography.Text>
        )}
      </Card>

      <Card title="事件">
        {detail.events.length === 0 ? (
          <Typography.Text type="secondary">暂无事件</Typography.Text>
        ) : (
          <Timeline
            items={detail.events.map((event) => ({
              color: event.type.includes('unexpected') ? 'red' : 'blue',
              children: (
                <Space direction="vertical" size={0}>
                  <Typography.Text strong>{event.type}</Typography.Text>
                  <Typography.Text type="secondary">
                    {formatTime(event.createdAt)} · {event.actor}
                  </Typography.Text>
                  {event.reason && <Typography.Text>{event.reason}</Typography.Text>}
                </Space>
              ),
            }))}
          />
        )}
      </Card>
    </Space>
  )
}
```

- [ ] **Step 6: 运行测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/pages/__tests__/VolumeList.test.tsx`

Expected: 2 个测试通过。

- [ ] **Step 7: 类型检查**

Run（在 `frontend/`）: `npx tsc -b`

Expected: 通过。

- [ ] **Step 8: 提交**

```bash
git add frontend/src/components/volumes/VolumeLifecycleModal.tsx frontend/src/pages/VolumeList.tsx frontend/src/pages/VolumeDetail.tsx frontend/src/pages/__tests__/VolumeList.test.tsx
git commit -m "feat(volumes): add volume list and detail pages" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 租户、违规报告与回填导入页

**Files:**

- Create: `frontend/src/components/volumes/TenantFormModal.tsx`
- Create: `frontend/src/components/volumes/TenantKeysModal.tsx`
- Create: `frontend/src/pages/VolumeTenants.tsx`
- Create: `frontend/src/pages/VolumeReport.tsx`
- Create: `frontend/src/pages/VolumeImport.tsx`
- Create: `frontend/src/pages/__tests__/VolumeImport.test.tsx`

**Interfaces:**

- Consumes: Task 2 `volumeApi`、`parseVolumeCsv`；Plan A `useAuth`
- Produces: `/volume-tenants`、`/volume-report`、`/volume-import` 页面组件

- [ ] **Step 1: 写导入页失败测试**

新建 `frontend/src/pages/__tests__/VolumeImport.test.tsx`：

```tsx
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import { volumeApi } from '@/api/volume'
import VolumeImport from '../VolumeImport'

vi.mock('@/api/volume', () => ({
  volumeApi: { import: vi.fn() },
}))

const CSV = [
  'tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId',
  'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,/workspace,business,sb-1',
].join('\n')

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <VolumeImport />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeImport', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.import).mockResolvedValue({
      dryRun: true,
      results: [
        {
          namespace: 'opensandbox',
          claimName: 'pvc-1',
          status: 'would-register',
        },
      ],
    })
  })

  it('parses CSV and calls dry-run before allowing write', async () => {
    renderPage()

    fireEvent.change(screen.getByPlaceholderText(/粘贴 CSV/), {
      target: { value: CSV },
    })
    fireEvent.click(screen.getByRole('button', { name: '解析预览' }))

    expect(await screen.findByText('pvc-1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确认写入' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: 'Dry run' }))

    await waitFor(() =>
      expect(volumeApi.import).toHaveBeenCalledWith({
        dryRun: true,
        items: [expect.objectContaining({ claimName: 'pvc-1' })],
      }),
    )
  })

  it('shows parse errors without calling the API', () => {
    renderPage()

    fireEvent.change(screen.getByPlaceholderText(/粘贴 CSV/), {
      target: { value: 'tenant,namespace\nbad,row' },
    })
    fireEvent.click(screen.getByRole('button', { name: '解析预览' }))

    expect(screen.getByText(/缺少表头/)).toBeInTheDocument()
    expect(volumeApi.import).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/pages/__tests__/VolumeImport.test.tsx`

Expected: 编译失败，提示找不到 `../VolumeImport`。

- [ ] **Step 3: 实现租户表单与 Key 弹窗**

新建 `frontend/src/components/volumes/TenantFormModal.tsx`：

```tsx
import { useEffect } from 'react'
import { App, Form, Input, Modal, Select } from 'antd'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'

interface TenantFormModalProps {
  tenant: VolumeTenant | null
  open: boolean
  onClose: () => void
  onSaved: () => void
}

interface FormValues {
  id: string
  displayName: string
  status: 'active' | 'disabled'
  namespaces: string[]
}

export default function TenantFormModal({
  tenant,
  open,
  onClose,
  onSaved,
}: TenantFormModalProps) {
  const { message } = App.useApp()
  const [form] = Form.useForm<FormValues>()

  useEffect(() => {
    if (!open) return
    if (tenant) {
      form.setFieldsValue({
        id: tenant.id,
        displayName: tenant.displayName,
        status: tenant.status === 'disabled' ? 'disabled' : 'active',
        namespaces: tenant.namespaces,
      })
    } else {
      form.resetFields()
    }
  }, [open, tenant, form])

  const mutation = useMutation({
    mutationFn: (values: FormValues) =>
      tenant
        ? volumeApi.updateTenant(tenant.id, {
            displayName: values.displayName,
            status: values.status,
            namespaces: values.namespaces,
          })
        : volumeApi.createTenant({
            id: values.id,
            displayName: values.displayName,
            namespaces: values.namespaces,
          }),
    onSuccess: () => {
      message.success(tenant ? '租户已更新' : '租户已创建')
      onSaved()
      onClose()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  return (
    <Modal
      open={open}
      title={tenant ? '编辑租户' : '新建租户'}
      okText="保存"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => form.submit()}
    >
      <Form
        form={form}
        layout="vertical"
        initialValues={{ status: 'active', namespaces: [] }}
        onFinish={(values) => mutation.mutate(values)}
      >
        <Form.Item
          name="id"
          label="租户 ID"
          rules={[
            { required: true, message: '请输入租户 ID' },
            {
              pattern: /^[a-z0-9][a-z0-9-]{0,62}$/,
              message: '小写字母、数字或连字符',
            },
          ]}
        >
          <Input disabled={tenant !== null} placeholder="goodcrew" />
        </Form.Item>
        <Form.Item
          name="displayName"
          label="显示名"
          rules={[{ required: true, message: '请输入显示名' }]}
        >
          <Input placeholder="GoodCrew" maxLength={128} />
        </Form.Item>
        {tenant && (
          <Form.Item name="status" label="状态">
            <Select
              options={[
                { value: 'active', label: '启用' },
                { value: 'disabled', label: '停用' },
              ]}
            />
          </Form.Item>
        )}
        <Form.Item
          name="namespaces"
          label="命名空间"
          rules={[{ required: true, message: '至少一个命名空间' }]}
        >
          <Select mode="tags" tokenSeparators={[',']} placeholder="opensandbox" />
        </Form.Item>
      </Form>
    </Modal>
  )
}
```

新建 `frontend/src/components/volumes/TenantKeysModal.tsx`：

```tsx
import { useState } from 'react'
import {
  Alert,
  App,
  Button,
  Divider,
  Empty,
  Form,
  Input,
  List,
  Modal,
  Space,
  Tag,
  Typography,
} from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { formatTime } from '@/utils/format'

interface TenantKeysModalProps {
  tenant: VolumeTenant | null
  open: boolean
  canManage: boolean
  onClose: () => void
}

export default function TenantKeysModal({
  tenant,
  open,
  canManage,
  onClose,
}: TenantKeysModalProps) {
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [rawKey, setRawKey] = useState<string | null>(null)
  const tenantId = tenant?.id ?? ''

  const keysQuery = useQuery({
    queryKey: ['volume-tenants', tenantId, 'keys'],
    queryFn: () => volumeApi.keys(tenantId),
    enabled: open && tenantId.length > 0,
  })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['volume-tenants'] })
    void keysQuery.refetch()
  }

  const createMutation = useMutation({
    mutationFn: () =>
      volumeApi.createKey(tenantId, { name: name.trim() || 'default' }),
    onSuccess: (created) => {
      setRawKey(created.key)
      setName('')
      refresh()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const revokeMutation = useMutation({
    mutationFn: (keyId: string) => volumeApi.revokeKey(tenantId, keyId),
    onSuccess: () => {
      message.success('Key 已吊销')
      refresh()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const confirmRevoke = (keyId: string, prefix: string) => {
    modal.confirm({
      title: '吊销 Key',
      content: `确定吊销 ${prefix} 开头的 Key？调用方将立即失效。`,
      okText: '吊销',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => revokeMutation.mutateAsync(keyId),
    })
  }

  return (
    <Modal
      open={open}
      title={`租户 Key - ${tenant?.displayName ?? ''}`}
      footer={null}
      width={720}
      onCancel={() => {
        setRawKey(null)
        onClose()
      }}
    >
      {rawKey && (
        <Alert
          type="success"
          showIcon
          message="新 Key 只显示一次"
          description={
            <Typography.Paragraph code copyable={{ text: rawKey }} style={{ margin: 0 }}>
              {rawKey}
            </Typography.Paragraph>
          }
          action={
            <Button size="small" onClick={() => setRawKey(null)}>
              我已保存
            </Button>
          }
          style={{ marginBottom: 16 }}
        />
      )}

      {canManage && (
        <Form layout="inline" onFinish={() => createMutation.mutate()} style={{ marginBottom: 16 }}>
          <Form.Item>
            <Input
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="Key 名称（如 ci）"
              maxLength={64}
            />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={createMutation.isPending}>
              签发 Key
            </Button>
          </Form.Item>
        </Form>
      )}

      <Divider />

      <List
        loading={keysQuery.isLoading}
        dataSource={keysQuery.data ?? []}
        locale={{ emptyText: <Empty description="暂无 Key" /> }}
        renderItem={(key) => (
          <List.Item
            actions={[
              key.revokedAt ? (
                <Tag key="revoked">已吊销</Tag>
              ) : canManage ? (
                <Button
                  key="revoke"
                  danger
                  type="link"
                  onClick={() => confirmRevoke(key.id, key.prefix)}
                >
                  吊销
                </Button>
              ) : null,
            ]}
          >
            <List.Item.Meta
              title={
                <Space>
                  <Typography.Text code>{key.prefix}…</Typography.Text>
                  <span>{key.name}</span>
                </Space>
              }
              description={`创建 ${formatTime(key.createdAt)} · 最近使用 ${formatTime(key.lastUsedAt)}`}
            />
          </List.Item>
        )}
      />
    </Modal>
  )
}
```

- [ ] **Step 4: 实现租户页**

新建 `frontend/src/pages/VolumeTenants.tsx`：

```tsx
import { useState } from 'react'
import { Button, Card, Space, Table, Tag } from 'antd'
import type { TableProps } from 'antd'
import { KeyOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import TenantFormModal from '@/components/volumes/TenantFormModal'
import TenantKeysModal from '@/components/volumes/TenantKeysModal'
import { formatTime } from '@/utils/format'

export default function VolumeTenants() {
  const { can } = useAuth()
  const [formTenant, setFormTenant] = useState<VolumeTenant | null>(null)
  const [formOpen, setFormOpen] = useState(false)
  const [keysTenant, setKeysTenant] = useState<VolumeTenant | null>(null)
  const canManage = can('volume:admin')

  const tenantsQuery = useQuery({
    queryKey: ['volume-tenants'],
    queryFn: volumeApi.tenants,
  })

  const columns: TableProps<VolumeTenant>['columns'] = [
    { title: '租户 ID', dataIndex: 'id', key: 'id', width: 150 },
    { title: '显示名', dataIndex: 'displayName', key: 'displayName', width: 160 },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 90,
      render: (value: string) => (
        <Tag color={value === 'active' ? 'green' : 'red'}>
          {value === 'active' ? '启用' : '停用'}
        </Tag>
      ),
    },
    {
      title: '命名空间',
      dataIndex: 'namespaces',
      key: 'namespaces',
      render: (values: string[]) => (
        <Space wrap>
          {values.map((value) => (
            <Tag key={value}>{value}</Tag>
          ))}
        </Space>
      ),
    },
    { title: '卷', dataIndex: 'volumeCount', key: 'volumeCount', width: 70 },
    { title: '业务卷', dataIndex: 'protectedCount', key: 'protectedCount', width: 80 },
    { title: '临时卷', dataIndex: 'ephemeralCount', key: 'ephemeralCount', width: 80 },
    { title: '缺失', dataIndex: 'missingCount', key: 'missingCount', width: 70 },
    { title: 'Key', dataIndex: 'keyCount', key: 'keyCount', width: 70 },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      render: (value: string) => formatTime(value),
    },
    {
      title: '操作',
      key: 'actions',
      width: 190,
      render: (_, record) => (
        <Space>
          <Button
            type="link"
            icon={<KeyOutlined />}
            onClick={() => setKeysTenant(record)}
          >
            Key
          </Button>
          {canManage && (
            <Button
              type="link"
              onClick={() => {
                setFormTenant(record)
                setFormOpen(true)
              }}
            >
              编辑
            </Button>
          )}
        </Space>
      ),
    },
  ]

  return (
    <Card
      title="租户管理"
      extra={
        canManage && (
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => {
              setFormTenant(null)
              setFormOpen(true)
            }}
          >
            新建租户
          </Button>
        )
      }
    >
      <Table<VolumeTenant>
        rowKey="id"
        loading={tenantsQuery.isLoading}
        columns={columns}
        dataSource={tenantsQuery.data ?? []}
        scroll={{ x: 1300 }}
        pagination={{ pageSize: 20, showTotal: (total) => `共 ${total} 个租户` }}
        locale={{ emptyText: tenantsQuery.isError ? '加载失败' : '暂无租户' }}
      />

      <TenantFormModal
        tenant={formTenant}
        open={formOpen}
        onClose={() => setFormOpen(false)}
        onSaved={() => tenantsQuery.refetch()}
      />
      <TenantKeysModal
        tenant={keysTenant}
        open={keysTenant !== null}
        canManage={canManage}
        onClose={() => setKeysTenant(null)}
      />
    </Card>
  )
}
```

- [ ] **Step 5: 实现违规报告页**

新建 `frontend/src/pages/VolumeReport.tsx`：

```tsx
import { App, Alert, Button, Card, Col, Empty, Progress, Row, Space, Statistic, Table, Tag, Typography } from 'antd'
import type { TableProps } from 'antd'
import { ScanOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { ScanError, VolumeDistributionItem, VolumeViolation } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import { formatTime } from '@/utils/format'

export default function VolumeReport() {
  const { can } = useAuth()
  const { message } = App.useApp()
  const queryClient = useQueryClient()

  const reportQuery = useQuery({
    queryKey: ['volumes', 'report'],
    queryFn: volumeApi.report,
  })

  const scanMutation = useMutation({
    mutationFn: volumeApi.scan,
    onSuccess: () => {
      message.success('扫描已完成')
      queryClient.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const report = reportQuery.data
  const counts = report?.counts
  const coverage = report?.coverage

  const distributionColumns: TableProps<VolumeDistributionItem>['columns'] = [
    { title: '名称', dataIndex: 'key', key: 'key' },
    { title: '数量', dataIndex: 'count', key: 'count', width: 100 },
  ]

  const violationColumns: TableProps<VolumeViolation>['columns'] = [
    {
      title: '类型',
      dataIndex: 'type',
      key: 'type',
      width: 180,
      render: (value: string) => <Tag>{value}</Tag>,
    },
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 160 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 200 },
    { title: '说明', dataIndex: 'message', key: 'message' },
  ]

  const errorColumns: TableProps<ScanError>['columns'] = [
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 200 },
    { title: '错误', dataIndex: 'message', key: 'message' },
  ]

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card
        title="卷治理报告"
        extra={
          can('volume:admin') && (
            <Button
              type="primary"
              icon={<ScanOutlined />}
              loading={scanMutation.isPending}
              onClick={() => scanMutation.mutate()}
            >
              立即扫描
            </Button>
          )
        }
      >
        {reportQuery.isError && (
          <Alert
            type="error"
            showIcon
            message={extractErrorMessage(reportQuery.error)}
            style={{ marginBottom: 16 }}
          />
        )}

        <Row gutter={[16, 16]}>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="观测卷" value={counts?.observed ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="已登记" value={counts?.registered ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="未标注" value={counts?.unlabeled ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="未登记" value={counts?.unregistered ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="漂移" value={counts?.drift ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="缺失" value={counts?.missing ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="新 UID 重现" value={counts?.reappeared ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="失管" value={counts?.unmanaged ?? 0} />
          </Col>
        </Row>

        <Row gutter={[24, 16]} style={{ marginTop: 16 }}>
          <Col xs={24} md={12}>
            <Typography.Text>标签覆盖率</Typography.Text>
            <Progress percent={Number(coverage?.labeledPercent ?? 0)} />
          </Col>
          <Col xs={24} md={12}>
            <Typography.Text>登记覆盖率</Typography.Text>
            <Progress percent={Number(coverage?.registeredPercent ?? 0)} />
          </Col>
        </Row>

        <Space wrap size={24} style={{ marginTop: 16 }}>
          <Typography.Text>
            最后扫描：{formatTime(report?.lastScan?.finishedAt)}
          </Typography.Text>
          <Typography.Text>触发方式：{report?.lastScan?.trigger ?? '-'}</Typography.Text>
          <Typography.Text>
            命名空间：{report?.lastScan?.namespacesSucceeded ?? 0}/
            {report?.lastScan?.namespacesScanned ?? 0}
          </Typography.Text>
        </Space>
      </Card>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card title="按租户分布">
            <Table<VolumeDistributionItem>
              rowKey="key"
              size="small"
              columns={distributionColumns}
              dataSource={report?.byTenant ?? []}
              pagination={false}
            />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title="按命名空间分布">
            <Table<VolumeDistributionItem>
              rowKey="key"
              size="small"
              columns={distributionColumns}
              dataSource={report?.byNamespace ?? []}
              pagination={false}
            />
          </Card>
        </Col>
      </Row>

      <Card
        title={`违规明细${report?.truncated ? '（已截断为前 200 条）' : ''}`}
      >
        {report?.truncated && (
          <Alert
            type="warning"
            showIcon
            message="违规超过 200 条，仅展示前 200 条；请先处理未标注与未登记卷。"
            style={{ marginBottom: 16 }}
          />
        )}
        <Table<VolumeViolation>
          rowKey={(row) => `${row.type}/${row.namespace}/${row.claimName}`}
          size="small"
          columns={violationColumns}
          dataSource={report?.violations ?? []}
          pagination={{ pageSize: 20 }}
          locale={{ emptyText: <Empty description="没有违规" /> }}
        />
      </Card>

      <Card title="上次扫描错误">
        <Table<ScanError>
          rowKey={(row) => `${row.namespace}/${row.message}`}
          size="small"
          columns={errorColumns}
          dataSource={report?.lastScan?.errors ?? []}
          pagination={false}
          locale={{ emptyText: <Empty description="没有扫描错误" /> }}
        />
      </Card>
    </Space>
  )
}
```

- [ ] **Step 6: 实现回填导入页**

新建 `frontend/src/pages/VolumeImport.tsx`：

```tsx
import { useState } from 'react'
import { Alert, App, Button, Card, Input, Space, Table, Tag, Typography, Upload } from 'antd'
import type { TableProps, UploadProps } from 'antd'
import { InboxOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeImportItem, VolumeImportResponse, VolumeImportResult } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { parseVolumeCsv, type VolumeCsvParseResult } from '@/utils/volumeCsv'

const STATUS_COLORS: Record<string, string> = {
  registered: 'green',
  updated: 'blue',
  'label-pending': 'orange',
  conflict: 'red',
  invalid: 'red',
  'not-found': 'magenta',
  'would-register': 'cyan',
  'would-update': 'geekblue',
}

const STATUS_LABELS: Record<string, string> = {
  registered: '已登记',
  updated: '已更新',
  'label-pending': '标签待重试',
  conflict: '冲突',
  invalid: '无效',
  'not-found': 'PVC 不存在',
  'would-register': '将登记',
  'would-update': '将更新',
}

export default function VolumeImport() {
  const { message } = App.useApp()
  const [csvText, setCsvText] = useState('')
  const [parsed, setParsed] = useState<VolumeCsvParseResult | null>(null)
  const [dryRunResult, setDryRunResult] = useState<VolumeImportResponse | null>(null)

  const reset = () => {
    setParsed(null)
    setDryRunResult(null)
  }

  const dryRunMutation = useMutation({
    mutationFn: () =>
      volumeApi.import({ dryRun: true, items: parsed?.items ?? [] }),
    onSuccess: (result) => setDryRunResult(result),
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const writeMutation = useMutation({
    mutationFn: () =>
      volumeApi.import({ dryRun: false, items: parsed?.items ?? [] }),
    onSuccess: (result) => {
      setDryRunResult(result)
      message.success('导入完成')
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const uploadProps: UploadProps = {
    accept: '.csv,text/csv',
    showUploadList: false,
    beforeUpload: async (file) => {
      setCsvText(await file.text())
      reset()
      return false
    },
  }

  const previewColumns: TableProps<VolumeImportItem>['columns'] = [
    { title: '租户', dataIndex: 'tenant', key: 'tenant', width: 110 },
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 140 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 180 },
    {
      title: 'Owner',
      key: 'owner',
      render: (_, record) => `${record.ownerKind}/${record.ownerId}`,
    },
    { title: 'Lifecycle', dataIndex: 'lifecycle', key: 'lifecycle', width: 110 },
    { title: '挂载路径', dataIndex: 'mountPath', key: 'mountPath' },
  ]

  const resultColumns: TableProps<VolumeImportResult>['columns'] = [
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 150 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 200 },
    {
      title: '结果',
      dataIndex: 'status',
      key: 'status',
      width: 130,
      render: (value: string) => (
        <Tag color={STATUS_COLORS[value] ?? 'default'}>{STATUS_LABELS[value] ?? value}</Tag>
      ),
    },
    { title: '说明', dataIndex: 'message', key: 'message', render: (value?: string) => value ?? '-' },
  ]

  const canWrite =
    parsed !== null &&
    parsed.items.length > 0 &&
    dryRunResult?.dryRun === true &&
    !writeMutation.isPending

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card title="CSV 回填导入">
        <Alert
          type="info"
          showIcon
          message="流程：粘贴或上传 CSV → 解析预览 → Dry run → 确认写入"
          description="表头固定为 tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId；导入不会覆盖其他租户的归属。"
          style={{ marginBottom: 16 }}
        />

        <Upload.Dragger {...uploadProps} style={{ marginBottom: 16 }}>
          <p className="ant-upload-drag-icon">
            <InboxOutlined />
          </p>
          <p className="ant-upload-text">点击或拖拽 CSV 到此处</p>
          <p className="ant-upload-hint">也可以直接粘贴到下方文本框</p>
        </Upload.Dragger>

        <Input.TextArea
          value={csvText}
          onChange={(event) => {
            setCsvText(event.target.value)
            reset()
          }}
          rows={8}
          placeholder="粘贴 CSV，第一行为表头"
          style={{ fontFamily: 'monospace' }}
        />

        <Space style={{ marginTop: 16 }}>
          <Button onClick={() => setParsed(parseVolumeCsv(csvText))}>解析预览</Button>
          <Button
            type="primary"
            disabled={!parsed || parsed.items.length === 0}
            loading={dryRunMutation.isPending}
            onClick={() => dryRunMutation.mutate()}
          >
            Dry run
          </Button>
          <Button
            danger
            type="primary"
            disabled={!canWrite}
            loading={writeMutation.isPending}
            onClick={() => writeMutation.mutate()}
          >
            确认写入
          </Button>
          {parsed && (
            <Typography.Text type="secondary">
              有效 {parsed.items.length} 行，错误 {parsed.errors.length} 行
            </Typography.Text>
          )}
        </Space>

        {parsed && parsed.errors.length > 0 && (
          <Alert
            type="error"
            showIcon
            message="CSV 存在错误"
            description={
              <ul style={{ margin: 0, paddingLeft: 20 }}>
                {parsed.errors.map((error) => (
                  <li key={error}>{error}</li>
                ))}
              </ul>
            }
            style={{ marginTop: 16 }}
          />
        )}
      </Card>

      {parsed && parsed.items.length > 0 && (
        <Card title="预览">
          <Table<VolumeImportItem>
            rowKey={(row) => `${row.namespace}/${row.claimName}`}
            size="small"
            columns={previewColumns}
            dataSource={parsed.items}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 900 }}
          />
        </Card>
      )}

      {dryRunResult && (
        <Card
          title={dryRunResult.dryRun ? 'Dry run 结果（未写入）' : '写入结果'}
        >
          <Table<VolumeImportResult>
            rowKey={(row) => `${row.namespace}/${row.claimName}`}
            size="small"
            columns={resultColumns}
            dataSource={dryRunResult.results}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 700 }}
          />
        </Card>
      )}
    </Space>
  )
}
```

- [ ] **Step 7: 运行导入测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/pages/__tests__/VolumeImport.test.tsx`

Expected: 2 个测试通过。

- [ ] **Step 8: 类型检查**

Run（在 `frontend/`）: `npx tsc -b`

Expected: 通过。

- [ ] **Step 9: 提交**

```bash
git add frontend/src/components/volumes/TenantFormModal.tsx frontend/src/components/volumes/TenantKeysModal.tsx frontend/src/pages/VolumeTenants.tsx frontend/src/pages/VolumeReport.tsx frontend/src/pages/VolumeImport.tsx frontend/src/pages/__tests__/VolumeImport.test.tsx
git commit -m "feat(volumes): add tenant, report and import pages" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 角色守卫、根路由与卷管理菜单

**Files:**

- Modify: `frontend/src/auth/AuthContext.tsx`
- Modify: `frontend/src/auth/NoPermission.tsx`
- Modify: `frontend/src/auth/RequireSandboxAdmin.tsx`
- Create: `frontend/src/auth/RequireSandboxViewer.tsx`
- Create: `frontend/src/auth/RequireVolumeRole.tsx`
- Create: `frontend/src/auth/RoleHome.tsx`
- Modify: `frontend/src/router/index.tsx`
- Modify: `frontend/src/components/layout/AppLayout.tsx`
- Modify: `frontend/src/components/layout/__tests__/AppLayout.test.tsx`
- Create: `frontend/src/auth/__tests__/RoleHome.test.tsx`
- Create: `frontend/src/auth/__tests__/RequireSandboxViewer.test.tsx`

**Interfaces:**

- Consumes: Plan A `useAuth()`、`hasConsoleRole`
- Produces: `RequireSandboxViewer`、`RequireVolumeViewer`、`RequireVolumeAdmin`、`RoleHome`

- [ ] **Step 1: 写根路由与菜单失败测试**

新建 `frontend/src/auth/__tests__/RoleHome.test.tsx`：

```tsx
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { useAuth } from '@/auth/AuthContext'
import RoleHome from '../RoleHome'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

const mockedUseAuth = vi.mocked(useAuth)

function renderRoot() {
  const router = createMemoryRouter(
    [
      { path: '/', element: <RoleHome /> },
      { path: '/dashboard', element: <div>dashboard-page</div> },
      { path: '/volumes', element: <div>volumes-page</div> },
      { path: '/settings', element: <div>settings-page</div> },
    ],
    { initialEntries: ['/'] },
  )
  return render(<RouterProvider router={router} />)
}

function auth(can: (role: string) => boolean) {
  return {
    me: { subject: 'user-1', username: 'alice', roles: [] },
    can,
  }
}

describe('RoleHome', () => {
  beforeEach(() => mockedUseAuth.mockReset())

  it('sends sandbox users to the dashboard', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'sandbox:viewer'))

    renderRoot()

    expect(await screen.findByText('dashboard-page')).toBeInTheDocument()
  })

  it('sends volume-only users to the volume list', async () => {
    mockedUseAuth.mockReturnValue(
      auth((role) => role === 'volume:viewer' || role === 'volume:admin'),
    )

    renderRoot()

    expect(await screen.findByText('volumes-page')).toBeInTheDocument()
  })
})
```

新建 `frontend/src/auth/__tests__/RequireSandboxViewer.test.tsx`：

```tsx
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { useAuth } from '@/auth/AuthContext'
import { RequireSandboxViewer } from '../RequireSandboxViewer'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

const mockedUseAuth = vi.mocked(useAuth)

function renderGuarded() {
  const router = createMemoryRouter(
    [
      { path: '/', element: <div>home-page</div> },
      {
        path: '/dashboard',
        element: (
          <RequireSandboxViewer>
            <div>dashboard-page</div>
          </RequireSandboxViewer>
        ),
      },
    ],
    { initialEntries: ['/dashboard'] },
  )
  return render(<RouterProvider router={router} />)
}

function auth(can: (role: string) => boolean) {
  return {
    me: { subject: 'user-1', username: 'alice', roles: [] },
    can,
  }
}

describe('RequireSandboxViewer', () => {
  beforeEach(() => mockedUseAuth.mockReset())

  it('renders sandbox pages for sandbox viewers', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'sandbox:viewer'))

    renderGuarded()

    expect(await screen.findByText('dashboard-page')).toBeInTheDocument()
  })

  it('redirects volume-only users home', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'volume:viewer'))

    renderGuarded()

    expect(await screen.findByText('home-page')).toBeInTheDocument()
  })
})
```

把 `frontend/src/components/layout/__tests__/AppLayout.test.tsx` 整体替换为：

```tsx
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import AppLayout from '../AppLayout'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: {
      subject: 'user-1',
      username: 'alice',
      roles: ['sandbox:admin', 'volume:admin'],
    },
    can: () => true,
  }),
}))

describe('AppLayout', () => {
  it('renders the volume menu group and role tags', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/',
          element: <AppLayout />,
          children: [{ index: true, element: <div>页面内容</div> }],
        },
      ],
      { initialEntries: ['/'] },
    )

    render(
      <ConfigProvider>
        <AntApp>
          <RouterProvider router={router} />
        </AntApp>
      </ConfigProvider>,
    )

    expect(screen.getByText('OpenSandbox Admin')).toBeInTheDocument()
    expect(screen.getByText('仪表盘')).toBeInTheDocument()
    expect(screen.getByText('卷管理')).toBeInTheDocument()
    expect(screen.getByText('卷列表')).toBeInTheDocument()
    expect(screen.getByText('租户管理')).toBeInTheDocument()
    expect(screen.getByText('违规报告')).toBeInTheDocument()
    expect(screen.getByText('回填导入')).toBeInTheDocument()
    expect(screen.getByText('volume:admin')).toBeInTheDocument()
    expect(screen.getByText('页面内容')).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/RoleHome.test.tsx src/auth/__tests__/RequireSandboxViewer.test.tsx src/components/layout/__tests__/AppLayout.test.tsx`

Expected: 编译失败，提示找不到 `../RoleHome` / `../RequireSandboxViewer`；`AppLayout` 断言找不到「卷管理」。

- [ ] **Step 3: 放宽启动门控并新增守卫**

修改 `frontend/src/auth/AuthContext.tsx`，把：

```tsx
  const hasSandboxRole =
    data.roles.includes('sandbox:admin') || data.roles.includes('sandbox:viewer')
  if (!hasSandboxRole) return <NoPermission me={data} />
```

替换为：

```tsx
  if (data.roles.length === 0) return <NoPermission me={data} />
```

把 `frontend/src/auth/NoPermission.tsx` 整体替换为：

```tsx
import { Button, Result } from 'antd'
import type { ConsoleMe } from '@/api/console'
import { logout } from './auth'

export default function NoPermission({ me }: { me: ConsoleMe }) {
  return (
    <Result
      status="403"
      title="没有控制台访问权限"
      subTitle={`账号 ${me.username} 没有 sandbox 或 volume 角色，请联系管理员。`}
      extra={<Button onClick={logout}>注销</Button>}
    />
  )
}
```

新建 `frontend/src/auth/RequireVolumeRole.tsx`：

```tsx
import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

function RequireRole({ role, children }: { role: string; children: ReactNode }) {
  const { can } = useAuth()
  if (!can(role)) return <Navigate to="/" replace />
  return <>{children}</>
}

export function RequireVolumeViewer({ children }: { children: ReactNode }) {
  return <RequireRole role="volume:viewer">{children}</RequireRole>
}

export function RequireVolumeAdmin({ children }: { children: ReactNode }) {
  return <RequireRole role="volume:admin">{children}</RequireRole>
}
```

新建 `frontend/src/auth/RequireSandboxViewer.tsx`：

```tsx
import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function RequireSandboxViewer({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  if (!can('sandbox:viewer')) return <Navigate to="/" replace />
  return <>{children}</>
}
```

把 `frontend/src/auth/RequireSandboxAdmin.tsx` 整体替换为（拒绝时回到根路由，由 `RoleHome` 按角色分流，避免把 volume-only 用户送进沙箱页面）：

```tsx
import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function RequireSandboxAdmin({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  if (!can('sandbox:admin')) return <Navigate to="/" replace />
  return <>{children}</>
}
```

新建 `frontend/src/auth/RoleHome.tsx`：

```tsx
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export default function RoleHome() {
  const { can } = useAuth()
  if (can('sandbox:viewer')) return <Navigate to="/dashboard" replace />
  if (can('volume:viewer')) return <Navigate to="/volumes" replace />
  return <Navigate to="/settings" replace />
}
```

- [ ] **Step 4: 接入路由**

把 `frontend/src/router/index.tsx` 整体替换为：

```tsx
import type { ReactNode } from 'react'
import { createBrowserRouter } from 'react-router-dom'
import RoleHome from '@/auth/RoleHome'
import { RequireVolumeAdmin, RequireVolumeViewer } from '@/auth/RequireVolumeRole'
import { RequireSandboxAdmin } from '@/auth/RequireSandboxAdmin'
import { RequireSandboxViewer } from '@/auth/RequireSandboxViewer'
import AppLayout from '@/components/layout/AppLayout'
import Dashboard from '@/pages/Dashboard'
import SandboxDetail from '@/pages/SandboxDetail'
import SandboxList from '@/pages/SandboxList'
import CreateSandbox from '@/pages/CreateSandbox'
import BatchCreate from '@/pages/BatchCreate'
import Settings from '@/pages/Settings'
import NotFound from '@/pages/NotFound'
import VolumeList from '@/pages/VolumeList'
import VolumeDetail from '@/pages/VolumeDetail'
import VolumeTenants from '@/pages/VolumeTenants'
import VolumeReport from '@/pages/VolumeReport'
import VolumeImport from '@/pages/VolumeImport'

const sandboxViewer = (element: ReactNode) => (
  <RequireSandboxViewer>{element}</RequireSandboxViewer>
)

const volumeViewer = (element: ReactNode) => (
  <RequireVolumeViewer>{element}</RequireVolumeViewer>
)

const volumeAdmin = (element: ReactNode) => (
  <RequireVolumeAdmin>{element}</RequireVolumeAdmin>
)

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <RoleHome /> },
      { path: 'dashboard', element: sandboxViewer(<Dashboard />) },
      { path: 'sandboxes', element: sandboxViewer(<SandboxList />) },
      { path: 'sandboxes/:id', element: sandboxViewer(<SandboxDetail />) },
      {
        path: 'create',
        element: (
          <RequireSandboxAdmin>
            <CreateSandbox />
          </RequireSandboxAdmin>
        ),
      },
      {
        path: 'batch-create',
        element: (
          <RequireSandboxAdmin>
            <BatchCreate />
          </RequireSandboxAdmin>
        ),
      },
      { path: 'volumes', element: volumeViewer(<VolumeList />) },
      {
        path: 'volumes/:namespace/:claimName',
        element: volumeViewer(<VolumeDetail />),
      },
      { path: 'volume-tenants', element: volumeViewer(<VolumeTenants />) },
      { path: 'volume-report', element: volumeViewer(<VolumeReport />) },
      { path: 'volume-import', element: volumeAdmin(<VolumeImport />) },
      { path: 'settings', element: <Settings /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])
```

- [ ] **Step 5: 更新布局菜单与角色标签**

把 `frontend/src/components/layout/AppLayout.tsx` 整体替换为：

```tsx
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Avatar, Dropdown, Layout, Menu, Space, Tag, Typography, theme } from 'antd'
import type { MenuProps } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  CloudUploadOutlined,
  DatabaseOutlined,
  LogoutOutlined,
  PlusOutlined,
  SettingOutlined,
  TeamOutlined,
  ThunderboltOutlined,
  UserOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { logout } from '@/auth/auth'
import { useAuth } from '@/auth/AuthContext'

const { Sider, Header, Content } = Layout

const SANDBOX_ITEMS = [
  { key: '/dashboard', icon: <AppstoreOutlined />, label: '仪表盘', role: 'sandbox:viewer' },
  { key: '/sandboxes', icon: <BarsOutlined />, label: '沙箱列表', role: 'sandbox:viewer' },
  { key: '/create', icon: <PlusOutlined />, label: '创建沙箱', role: 'sandbox:admin' },
  {
    key: '/batch-create',
    icon: <ThunderboltOutlined />,
    label: '批量创建',
    role: 'sandbox:admin',
  },
  { key: '/settings', icon: <SettingOutlined />, label: '设置', role: null },
] as const

const VOLUME_ITEMS = [
  { key: '/volumes', icon: <DatabaseOutlined />, label: '卷列表', role: 'volume:viewer' },
  { key: '/volume-tenants', icon: <TeamOutlined />, label: '租户管理', role: 'volume:viewer' },
  {
    key: '/volume-report',
    icon: <WarningOutlined />,
    label: '违规报告',
    role: 'volume:viewer',
  },
  {
    key: '/volume-import',
    icon: <CloudUploadOutlined />,
    label: '回填导入',
    role: 'volume:admin',
  },
] as const

type MenuEntry = (typeof SANDBOX_ITEMS)[number] | (typeof VOLUME_ITEMS)[number]

const toMenuItem = (item: MenuEntry) => ({
  key: item.key,
  icon: item.icon,
  label: item.label,
})

export default function AppLayout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { token } = theme.useToken()
  const { me, can } = useAuth()

  const visibleSandbox = SANDBOX_ITEMS.filter((item) => !item.role || can(item.role))
  const visibleVolume = VOLUME_ITEMS.filter((item) => can(item.role))

  const selectedKey =
    [...visibleSandbox, ...visibleVolume]
      .filter(
        (item) =>
          location.pathname === item.key ||
          location.pathname.startsWith(`${item.key}/`),
      )
      .sort((left, right) => right.key.length - left.key.length)[0]?.key ?? '/settings'

  const menuItems: MenuProps['items'] = [
    ...visibleSandbox.map(toMenuItem),
    ...(visibleVolume.length > 0
      ? [
          {
            type: 'group' as const,
            label: '卷管理',
            children: visibleVolume.map(toMenuItem),
          },
        ]
      : []),
  ]

  const userMenu: MenuProps['items'] = [
    { key: 'account', disabled: true, label: me.username },
    { type: 'divider' },
    { key: 'logout', icon: <LogoutOutlined />, label: '注销', onClick: logout },
  ]

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider width={220} theme="dark">
        <div
          style={{
            height: 56,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <Typography.Text strong style={{ color: '#fff', fontSize: 16 }}>
            OpenSandbox Admin
          </Typography.Text>
        </div>
        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[selectedKey]}
          items={menuItems}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            background: token.colorBgContainer,
            padding: '0 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <Typography.Title level={4} style={{ margin: 0 }}>
            OpenSandbox 管理控制台
          </Typography.Title>
          <Space size={8}>
            {me.roles.map((role) => (
              <Tag key={role} color={role.endsWith(':admin') ? 'red' : 'blue'}>
                {role}
              </Tag>
            ))}
            <Dropdown menu={{ items: userMenu }}>
              <Space style={{ cursor: 'pointer' }}>
                <Avatar size="small" icon={<UserOutlined />} />
                <Typography.Text>{me.username}</Typography.Text>
              </Space>
            </Dropdown>
          </Space>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
```

- [ ] **Step 6: 运行前端测试确认通过**

Run（在 `frontend/`）: `npx vitest run src/auth/__tests__/RoleHome.test.tsx src/auth/__tests__/RequireSandboxViewer.test.tsx src/components/layout/__tests__/AppLayout.test.tsx`

Expected: 全部通过。

- [ ] **Step 7: 跑全部前端测试与类型检查**

Run（在 `frontend/`）: `npm test`

Expected: 全部通过。

Run（在 `frontend/`）: `npx tsc -b`

Expected: 通过。

- [ ] **Step 8: 提交**

```bash
git add frontend/src/auth/AuthContext.tsx frontend/src/auth/NoPermission.tsx frontend/src/auth/RequireSandboxAdmin.tsx frontend/src/auth/RequireSandboxViewer.tsx frontend/src/auth/RequireVolumeRole.tsx frontend/src/auth/RoleHome.tsx frontend/src/router/index.tsx frontend/src/components/layout/AppLayout.tsx frontend/src/components/layout/__tests__/AppLayout.test.tsx frontend/src/auth/__tests__/RoleHome.test.tsx frontend/src/auth/__tests__/RequireSandboxViewer.test.tsx
git commit -m "feat(console): add volume routes and role-aware navigation" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: PostgreSQL、Keycloak 文档与运维手册

**Files:**

- Modify: `docker-compose.yml`
- Modify: `.env.example`
- Create: `deploy/volume-manager/keycloak-client.md`
- Create: `docs/volume-manager-operations.md`
- Modify: `README.md`

**Interfaces:**

- Consumes: Plan A 的 `Auth` 配置、Task 1 的清单、Plan B2 的 `ConnectionStrings:VolumeManager` / `VolumeManager` 配置
- Produces: 可照做的部署与运维入口

- [ ] **Step 1: 写 Keycloak 客户端文档**

新建 `deploy/volume-manager/keycloak-client.md`：

````markdown
# Keycloak 客户端配置（opensandbox-web）

控制台使用 Keycloak 公共客户端 + Authorization Code + PKCE S256。后端只信任
`resource_access.<ClientId>.roles` 中的客户端角色，不读取 realm roles。

## 1. 创建客户端

Keycloak Admin Console → 目标 realm → Clients → Create client：

| 配置 | 值 |
| --- | --- |
| Client ID | `opensandbox-web`（必须与 `Auth:ClientId` 完全一致） |
| Client authentication | Off |
| Authorization | Off |
| Standard flow | On |
| Direct access grants | Off |
| Implicit flow | Off |
| Service accounts roles | Off |

## 2. 启用 PKCE

Clients → `opensandbox-web` → Settings → Advanced：

- Proof Key for Code Exchange Code Challenge Method: `S256`

## 3. 配置 URL

Clients → `opensandbox-web` → Settings：

| 字段 | 值 |
| --- | --- |
| Valid redirect URIs | `https://<控制台域名>/*`；开发环境追加 `http://localhost:3000/*` |
| Valid post logout redirect URIs | `https://<控制台域名>/*`；开发环境追加 `http://localhost:3000/*` |
| Web origins | 控制台源；开发环境追加 `http://localhost:3000` |

token 端点是浏览器直连，Web origins / CORS 必须包含控制台源。

## 4. 创建客户端角色

Clients → `opensandbox-web` → Roles → Create role，逐个创建：

- `sandbox:admin`
- `sandbox:viewer`
- `volume:admin`
- `volume:viewer`

把角色分配给用户或组：Users → 用户 → Role mapping → Assign role →
Filter by clients → 选择上述角色。

`volume:admin` 隐含同分面 viewer；`sandbox:admin` 同理。两个分面互不隐含。

## 5. Token 生命周期

- Access token lifespan：5 分钟（前端提前 60 秒静默续期）
- SSO Session Idle / Max：按组织策略；刷新令牌失效即要求重新登录
- 不需要 offline token

## 6. 后端配置

```json
{
  "Auth": {
    "Authority": "https://<keycloak-host>/realms/<realm>",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  }
}
```

容器环境变量：

```bash
Auth__Authority=https://<keycloak-host>/realms/<realm>
Auth__ClientId=opensandbox-web
Auth__RequireHttpsMetadata=true
```

后端容器必须能访问 `Authority` 的 discovery / JWKS 端点；浏览器必须能访问授权、
token 与 logout 端点。`Auth:Authority` 与 `Auth:ClientId` 缺失时后端拒绝启动。

## 7. 验收

1. 无角色用户登录：看到「没有控制台访问权限」。
2. `sandbox:viewer` 用户登录：落到 `/dashboard`；访问 `/volumes` 显示无权限。
3. `volume:viewer` 用户登录：落到 `/volumes`；不能点「降级」「立即扫描」。
4. `volume:admin` 用户登录：可以变更 lifecycle、扫描、管理租户与 Key。
5. 匿名请求 `GET /api/v1/volumes` 返回 401；无 volume 角色的 JWT 返回 403。
6. 租户 API Key 调管理 API 返回 401；控制台 JWT 调 `/api/v1/tenant/volumes` 返回 401。
````

- [ ] **Step 2: 写运维手册**

新建 `docs/volume-manager-operations.md`：

````markdown
# VolumeManager 运维手册

## 1. 组件

| 组件 | 作用 |
| --- | --- |
| PostgreSQL 17 | 台账、租户、Key、扫描与事件 |
| VolumeManager 模块 | 扫描集群 PVC、对账、标签补写、报表与 API |
| Keycloak `opensandbox-web` | 控制台登录与四个分面角色 |
| Kubernetes RBAC / VAP | 只读扫描 + 标签 patch + 业务卷删除保护 |
| 租户 API Key | 业务侧注册/降级卷，不依赖 Keycloak |

配置项见 spec §6.11：`ConnectionStrings:VolumeManager`、`VolumeManager:Enabled`、
`VolumeManager:ScanInterval`、`VolumeManager:KubeConfigPath`、`Auth:*`。

## 2. 上线顺序

| 阶段 | 动作 | 验收 |
| --- | --- | --- |
| P0 | 创建 Keycloak client/角色；部署 PostgreSQL；配置 `Auth:*` 与连接串 | 管理员能登录；匿名管理 API 401 |
| P0 | 部署 RBAC；给受管命名空间打标；`VolumeManager:Enabled=true` | 列表/报告可读，未标注卷完整 |
| P1 | 创建租户与 Key；业务接入注册；导入存量 | 覆盖率上升，冲突可解释 |
| P2 | 部署 VAP+Binding，保持 `Warn + Audit` | 审计日志中的「本会拦截」全部符合预期 |
| P3 | Binding 改为 `Deny` | 业务卷误删被拒；降级后删除成功 |

P2 观察期不可跳过。

## 3. PostgreSQL

Compose 已内置 `postgres:17`，凭据由 `.env` 的 `POSTGRES_PASSWORD` 控制。
外部数据库使用连接串：

```bash
ConnectionStrings__VolumeManager="Host=<host>;Port=5432;Database=opensandbox;Username=opensandbox;Password=<secret>"
```

- `VolumeManager:MigrateOnStartup=true` 时后端启动执行 EF 迁移；生产也可用独立 Job。
- 备份：`pg_dump -Fc -d opensandbox -f volume-manager.dump`
- 恢复：`pg_restore -d opensandbox --clean --if-exists volume-manager.dump`
- 不要手工改台账归属；冲突应通过重新注册或管理 API 处理。

## 4. Kubernetes RBAC

```bash
kubectl apply -f deploy/volume-manager/rbac.yaml
kubectl auth can-i list persistentvolumeclaims \
  --as=system:serviceaccount:opensandbox:opensandbox-volume-manager -n opensandbox
kubectl auth can-i patch persistentvolumeclaims \
  --as=system:serviceaccount:opensandbox:opensandbox-volume-manager -n opensandbox
kubectl auth can-i delete persistentvolumeclaims \
  --as=system:serviceaccount:opensandbox:opensandbox-volume-manager -n opensandbox
```

预期：前两条 `yes`，最后一条 `no`。

## 5. 受管命名空间

```bash
kubectl label namespace opensandbox opensandbox.io/volume-managed=true
kubectl get namespaces -l opensandbox.io/volume-managed=true
```

移除标签会让该命名空间的台账进入 `unmanaged`，不删除任何 PVC。

## 6. 租户接入

1. 控制台「租户管理」新建租户，填写显示名与命名空间。
2. 打开「Key」签发 Key；明文只显示一次，立即交给业务方保存。
3. 业务调用注册：

```bash
curl -sS -X POST "https://<console>/api/v1/tenant/volumes/register" \
  -H "Authorization: Bearer $TENANT_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "namespace": "opensandbox",
    "volumes": [{
      "claimName": "pvc-goodcrew-3f2a",
      "ownerKind": "employee",
      "ownerId": "3f2a",
      "role": "meta",
      "mountPath": "/workspace",
      "lifecycle": "business",
      "sandboxId": "sb-123"
    }]
  }'
```

4. 降级：

```bash
curl -sS -X POST "https://<console>/api/v1/tenant/volumes/opensandbox/pvc-goodcrew-3f2a/lifecycle" \
  -H "Authorization: Bearer $TENANT_KEY" \
  -H "Content-Type: application/json" \
  -d '{"lifecycle":"ephemeral","reason":"业务确认工作区不再保留"}'
```

`reason` 只对降级必填，上限 512 字符。降级成功后 VAP 才允许删除该 PVC。

5. 查看自己的卷：`GET /api/v1/tenant/volumes?page=1&pageSize=20`（强制租户过滤）。

Key 轮换：签发新 Key → 业务切换 → 确认旧 Key 的 `LastUsedAt` 不再变化 → 吊销旧 Key。
`LastUsedAt` 最多每 5 分钟更新一次。

## 7. 扫描与报告

- 后台每 `ScanInterval`（默认 5 分钟）扫描一次；`ScanOnStartup` 控制启动扫描。
- 控制台「立即扫描」是同步请求；已有扫描时返回 `409 scan_in_progress`。
- 某命名空间 list 失败时，该命名空间本轮不迁移任何状态，错误写入 `scan_runs.ErrorsJson`。
- K8s 不可用不阻断 `/readyz`；台账与历史报告仍可读。

常用 SQL（只读）：

```sql
select state, state_reason, count(*) from volumes group by 1, 2 order by 3 desc;
select classification, count(*) from (
  select case when lifecycle is null then 'unlabeled' else 'labeled' end as classification
  from cluster_volumes
) t group by 1;
select started_at, namespaces_scanned, namespaces_succeeded, volume_count
from scan_runs order by started_at desc limit 10;
```

## 8. 存量导入

控制台「回填导入」流程：CSV/粘贴 → 解析预览 → Dry run → 确认写入。

- 默认 dry-run；dry-run 不写数据库、不 patch 标签。
- 同一 `(namespace, claimName)` 已有台账但租户/owner/lifecycle 不一致 → `conflict`，不覆盖。
- `owner` 已绑定其他 PVC → `conflict`。
- 命名空间未分配、未打受管标签、PVC 不存在 → `invalid` / `not-found`。
- 写入后标签 patch 失败 → 台账保留，状态 `label-pending`，下轮扫描重试。

CSV 表头：

```csv
tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId
```

## 9. 删除保护

### P2：观察

```bash
kubectl apply -f deploy/volume-manager/protection-policy.yaml
kubectl get validatingadmissionpolicybinding opensandbox-protect-business-volumes -o yaml
```

确认 `validationActions: ["Warn", "Audit"]`。在 API Server 审计日志中检查
`opensandbox-protect-business-volumes` 的 deny 预判记录；所有「本会拦截」的删除都应是
应拦截的业务卷。观察期内业务必须改用降级端点。

### P3：拒绝

```bash
kubectl patch validatingadmissionpolicybinding opensandbox-protect-business-volumes \
  --type=merge -p '{"spec":{"validationActions":["Deny"]}}'
```

验证：

```bash
bash deploy/volume-manager/kind-protection-test.sh
```

要点：

- 保护条件是「标签存在且不等于 `ephemeral`」；未打 lifecycle 标签的卷默认**不**保护（P4 前）。
- 审批组固定为 `opensandbox-volume-approvers`；成员来自认证层（OIDC group claim 等），
  业务租户不入组。
- `system:masters` 不自动豁免。
- 逃生通道：移除 `opensandbox.io/volume-lifecycle` 标签（会进审计日志），或由审批组删除。
- 不要给 VolumeManager 的 ServiceAccount 授予 `delete`；它永远不删除 PVC。

## 10. 故障排查

| 现象 | 排查 |
| --- | --- |
| 控制台 401 | Keycloak `Auth:Authority`/`ClientId` 是否与后端一致；浏览器是否带 sessionStorage token |
| 控制台 403 | 用户是否在 `resource_access.opensandbox-web.roles` 中；角色名大小写 |
| 租户 API 401 | Key 是否吊销；租户是否 `disabled`；Authorization 是否 `Bearer <key>` |
| `503 dependency_unavailable` | PostgreSQL 是否可达；K8s API 是否可达 |
| 扫描一直失败 | `scan_runs.ErrorsJson`；ServiceAccount 是否可 list PVC/Pod；命名空间标签 |
| 卷显示 `unmanaged` | 命名空间是否移除受管标签；重新打标后下轮扫描恢复 |
| 状态 `label-pending` | K8s patch 权限；下轮扫描自动重试 |
| 导入全 `invalid` | 命名空间是否分配给租户；是否打受管标签 |
| 误删无法执行 | P3 已启用；先走降级端点，或由审批组操作 |
````

- [ ] **Step 3: 接入 PostgreSQL 与配置**

在 `docker-compose.yml` 的 `services:` 下、`opensandbox-server` 之前加入：

```yaml
  postgres:
    image: postgres:17
    environment:
      POSTGRES_DB: opensandbox
      POSTGRES_USER: opensandbox
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-opensandbox}
    ports:
      - "127.0.0.1:5432:5432"
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U opensandbox -d opensandbox"]
      interval: 5s
      timeout: 5s
      retries: 12
    restart: unless-stopped
```

把 `opensandbox-web.environment` 改为（保留 Plan A 的 `Auth__*`）：

```yaml
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      OpenSandbox__Domain: ${OPENSANDBOX_DOMAIN:-http://opensandbox-server:8090}
      OpenSandbox__ApiKey: ${OPENSANDBOX_API_KEY:-}
      OpenSandbox__RequestTimeoutSeconds: ${OPENSANDBOX_REQUEST_TIMEOUT_SECONDS:-30}
      Auth__Authority: ${AUTH_AUTHORITY:?AUTH_AUTHORITY is required}
      Auth__ClientId: ${AUTH_CLIENT_ID:-opensandbox-web}
      Auth__RequireHttpsMetadata: ${AUTH_REQUIRE_HTTPS_METADATA:-true}
      ConnectionStrings__VolumeManager: Host=postgres;Port=5432;Database=opensandbox;Username=opensandbox;Password=${POSTGRES_PASSWORD:-opensandbox}
      VolumeManager__Enabled: ${VOLUME_MANAGER_ENABLED:-true}
```

把 `depends_on` 改为：

```yaml
    depends_on:
      opensandbox-server:
        condition: service_started
      postgres:
        condition: service_healthy
```

在文件末尾 `networks:` 之后追加：

```yaml
volumes:
  postgres-data:
```

在 `.env.example` 末尾追加：

```bash
# VolumeManager 内置 PostgreSQL 密码（仅 compose 使用）
POSTGRES_PASSWORD=opensandbox
# 是否启用卷扫描；容器内没有 K8s API 访问时可设为 false，
# 台账/报告仍可读，但不会更新观测数据
VOLUME_MANAGER_ENABLED=true
```

- [ ] **Step 4: 更新 README 英文部分**

在 English 的 Features 列表末尾加入：

```markdown
- **Global Volume Governance** - Inventory PVCs across managed namespaces, register tenant ownership, detect label drift, report coverage, import existing volumes, and protect business volumes with Kubernetes admission policies
```

在 Tech Stack 表格的 Backend 行末尾补充 `, PostgreSQL 17, KubernetesClient`。

在 English 的 Configuration 段落末尾加入：

````markdown
Volume governance is configured through `ConnectionStrings:VolumeManager` and the
`VolumeManager` section:

```json
{
  "ConnectionStrings": {
    "VolumeManager": "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=..."
  },
  "VolumeManager": {
    "Enabled": true,
    "ScanInterval": "00:05:00",
    "ScanOnStartup": true,
    "ManagedNamespaceSelector": "opensandbox.io/volume-managed=true",
    "MaxConcurrentNamespaces": 4,
    "MigrateOnStartup": true
  }
}
```

The scanner needs a ServiceAccount with `namespaces get/list`,
`persistentvolumeclaims get/list/patch`, and `pods get/list`; apply
`deploy/volume-manager/rbac.yaml` and label managed namespaces with
`opensandbox.io/volume-managed=true`. Deletion protection and rollout order are
documented in `docs/volume-manager-operations.md`; Keycloak setup is in
`deploy/volume-manager/keycloak-client.md`.
````

在 English 的 API 表格末尾加入：

```markdown
| `/volumes` | GET | Volume inventory with filters, search, sort and pagination |
| `/volumes/{namespace}/{claimName}` | GET | Volume ledger, observation and event timeline |
| `/volumes/report` | GET | Coverage, classification counts and violations |
| `/volumes/scan` | POST | Trigger an inventory scan |
| `/volumes/import` | POST | Import existing volumes (dry-run by default) |
| `/volume-tenants` | GET/POST | Tenant list and creation |
| `/volume-tenants/{id}` | PATCH | Update a tenant |
| `/volume-tenants/{id}/keys` | GET/POST | List or issue tenant API keys |
| `/tenant/volumes/register` | POST | Tenant registration (API key auth) |
| `/tenant/volumes` | GET | Tenant-scoped volume list (API key auth) |
```

在 English 的 Docker 段落加入：

```markdown
- Docker Compose also starts PostgreSQL 17 (`postgres-data` volume). Set
  `POSTGRES_PASSWORD`, `AUTH_AUTHORITY`, and `VOLUME_MANAGER_ENABLED` in `.env`.
  Containers without Kubernetes API access should set `VOLUME_MANAGER_ENABLED=false`.
```

- [ ] **Step 5: 更新 README 中文部分**

在中文 Features 列表末尾加入：

```markdown
- **全局卷治理** - 跨受管命名空间盘点 PVC、登记租户归属、检测标签漂移、统计覆盖率、回填存量卷，并用 Kubernetes 准入策略保护业务卷
```

在技术栈表格的后端行末尾补充 `、PostgreSQL 17、KubernetesClient`。

在中文“配置”段落末尾加入：

````markdown
卷治理使用 `ConnectionStrings:VolumeManager` 与 `VolumeManager` 节：

```json
{
  "ConnectionStrings": {
    "VolumeManager": "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=..."
  },
  "VolumeManager": {
    "Enabled": true,
    "ScanInterval": "00:05:00",
    "ScanOnStartup": true,
    "ManagedNamespaceSelector": "opensandbox.io/volume-managed=true",
    "MaxConcurrentNamespaces": 4,
    "MigrateOnStartup": true
  }
}
```

扫描器需要 ServiceAccount 具备 `namespaces get/list`、
`persistentvolumeclaims get/list/patch`、`pods get/list`；先应用
`deploy/volume-manager/rbac.yaml`，再给受管命名空间打上
`opensandbox.io/volume-managed=true`。删除保护与上线顺序见
`docs/volume-manager-operations.md`，Keycloak 配置见
`deploy/volume-manager/keycloak-client.md`。
````

在中文 API 表格末尾加入：

```markdown
| `/volumes` | GET | 卷列表（筛选、搜索、排序、分页） |
| `/volumes/{namespace}/{claimName}` | GET | 台账、观测与事件时间线 |
| `/volumes/report` | GET | 覆盖率、分类统计与违规 |
| `/volumes/scan` | POST | 触发库存扫描 |
| `/volumes/import` | POST | 回填导入（默认 dry-run） |
| `/volume-tenants` | GET/POST | 租户列表与新建 |
| `/volume-tenants/{id}` | PATCH | 修改租户 |
| `/volume-tenants/{id}/keys` | GET/POST | 查看或签发租户 Key |
| `/tenant/volumes/register` | POST | 租户注册（API Key 认证） |
| `/tenant/volumes` | GET | 租户视角卷列表（API Key 认证） |
```

在中文 Docker 段落加入：

```markdown
- Docker Compose 同时启动 PostgreSQL 17（`postgres-data` 持久卷）。在 `.env` 中设置
  `POSTGRES_PASSWORD`、`AUTH_AUTHORITY`、`VOLUME_MANAGER_ENABLED`。容器无法访问
  Kubernetes API 时把 `VOLUME_MANAGER_ENABLED` 设为 `false`。
```

- [ ] **Step 6: 全量验证**

Run: `dotnet test tests/OpenSandbox.Web.Tests/OpenSandbox.Web.Tests.csproj`

Expected: 全部通过。

Run（在 `frontend/`）: `npm test`

Expected: 全部通过。

Run（在 `frontend/`）: `npm run lint`

Expected: 通过（若 oxlint 报未使用变量，删除对应 import，不要放宽规则）。

Run（在 `frontend/`）: `npm run build`

Expected: `tsc -b` 与 Vite 构建通过。

Run: `bash deploy/volume-manager/verify-manifests.sh`

Expected: `manifests ok`。

Run（有 Docker 时）: `bash deploy/volume-manager/kind-protection-test.sh`

Expected: `PASS: business PVC 删除被拒绝` 与 `PASS: ephemeral PVC 允许删除`。

- [ ] **Step 7: 提交**

```bash
git add docker-compose.yml .env.example deploy/volume-manager/keycloak-client.md docs/volume-manager-operations.md README.md
git commit -m "docs(volumes): add deployment and operations runbooks" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## 验收清单

- [ ] `deploy/volume-manager/rbac.yaml` 不含 `delete`/`create`/`update`，ServiceAccount、ClusterRole、ClusterRoleBinding 齐全。
- [ ] VAP 初始为 `Warn + Audit`，kind 测试证明 business PVC 删除被拒、ephemeral 可删。
- [ ] `/volumes` 支持 URL 同步的筛选/搜索/排序/服务端分页；viewer 看不到写操作。
- [ ] `/volumes/:namespace/:claimName` 展示台账、观测、原始标签与事件。
- [ ] `/volume-tenants` 可新建/编辑租户、签发并一次性展示 Key、吊销 Key；viewer 只读。
- [ ] `/volume-report` 展示分类计数、覆盖率、分布、违规与扫描错误；超过 200 条有截断提示。
- [ ] `/volume-import` 必须 dry-run 后才能写入；CSV 错误逐行提示；不覆盖其他租户。
- [ ] 无角色用户看到无权限页；sandbox 用户落 `/dashboard`；volume-only 用户落 `/volumes`。
- [ ] volume-only 用户直接访问 `/dashboard`、`/sandboxes`、`/sandboxes/:id` 会回到 `/volumes`；无 `sandbox:admin` 用户访问 `/create`、`/batch-create` 回到根路由。
- [ ] Header 显示用户名、角色标签与注销；菜单按角色隐藏。
- [ ] Compose 启动 PostgreSQL 17，后端连接串与迁移生效；`/healthz`、`/readyz` 匿名可用。
- [ ] 运维手册覆盖租户接入、Key 轮换、扫描、导入、删除保护 P2/P3 与故障排查。
- [ ] `dotnet test`、`npm test`、`npm run lint`、`npm run build`、清单校验全部通过。
