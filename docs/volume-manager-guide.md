# VolumeManager 技术文档

> 面向读者：平台运维、业务后端开发、控制台管理员
>
> 覆盖范围：全局卷台账、租户接入、盘点与对账、管理控制台、PVC 删除保护
>
> 最后更新：2026-10-26

## 0. 文档导航

| 文档 | 内容 |
| --- | --- |
| 本文 | 概念模型、快速起步、接入与管理 API、配置、常见问题 |
| [设计规范](superpowers/specs/2026-10-25-global-volume-manager-design.md) | 需求、状态机、冲突矩阵、安全模型的权威定义 |
| [运维手册](volume-manager-operations.md) | 上线顺序、PostgreSQL 备份、RBAC/VAP 操作、故障排查 |
| [Keycloak 客户端配置](../deploy/volume-manager/keycloak-client.md) | 创建 public client、PKCE、角色与验收 |
| [README](../README.md) | 仓库总览与开发环境启动 |

如果本文与设计规范冲突，以设计规范为准；部署与删除保护操作以运维手册为准。

---

## 1. 概述

VolumeManager 解决的是 Kubernetes 上 OpenSandbox 持久卷（PVC）的三个治理问题：

1. **归属不可见**：OpenSandbox 创建 PVC 时不写业务归属；跨命名空间后平台不知道卷属于哪个业务、哪个用户。
2. **存量不可盘点**：命名空间、卷数量、容量、挂载状态、标签漂移没有统一视图。
3. **误删没有独立防线**：一次错误的 `kubectl delete` 或错误的 selector 批量删除就可能清掉业务数据。

VolumeManager 的定位是**全局台账 + 独立删除保护 + 运营控制台**，不是卷生命周期执行器：

| 提供 | 不提供 |
| --- | --- |
| 租户/owner/lifecycle 的权威台账 | 删除 PVC/PV 的 API 或 UI |
| 注册时由平台代写 PVC 标签 | TTL、归档、reaper、自动回收 |
| 周期扫描、对账、违规报告 | 从卷名、ownerReference 或挂载关系推断归属 |
| 存量 CSV 回填导入 | 修改业务数据或业务自己的台账 |
| Keycloak 登录的分面角色控制台 | 控制台内的租户级数据隔离 |
| Kubernetes 准入策略的删除保护 | 消费 Kubernetes audit log 做归因 |

### 1.1 组件与边界

```text
业务后端 ──每租户 API Key──┐
                            ▼
                    ┌─────────────────────────────┐
                    │  VolumeManager（本仓库）     │
                    │  租户 API / 管理 API / 扫描 │
                    └───────┬──────────────┬──────┘
                            │              │
                    list/patch PVC    台账/观测/审计
                            │              │
                            ▼              ▼
                     Kubernetes API   PostgreSQL 17
                            │
                    ValidatingAdmissionPolicy（CEL）
                            │
                            ▼
                     PVC DELETE 保护

浏览器 ──Keycloak OIDC JWT──► 控制台与管理 API
```

关键边界：

- **保护在 apiserver 内执行**，不依赖 VolumeManager 进程存活。
- **组件永不删除卷**，Kubernetes RBAC 也没有 `delete` 权限。
- **归属只由业务显式声明**，平台不做推断。
- 业务自己的回收策略（例如 goodcrew 的 `VolumeReclaimPolicy`）保持不变。

### 1.2 组件清单

| 组件 | 作用 |
| --- | --- |
| OpenSandbox Web 后端 | 租户 API、管理 API、盘点 Worker、标签写入 |
| PostgreSQL 17 | 台账、观测、租户、API Key、扫描记录、审计事件 |
| Keycloak | 控制台登录与四个分面角色 |
| Kubernetes RBAC | 受管命名空间的只读扫描 + PVC 标签 `patch` |
| ValidatingAdmissionPolicy | 在 apiserver 内拦截业务 PVC 的 DELETE |
| 租户 API Key | 业务后端注册/降级卷，不依赖 Keycloak |

---

## 2. 核心概念

### 2.1 三套凭据，互不通用

| 平面 | 凭据 | 使用方 | 覆盖范围 |
| --- | --- | --- | --- |
| 控制台 / 管理 API | Keycloak access token（JWT） | 平台管理员、查看者 | `/api/v1/volumes*`、`/api/v1/volume-tenants*`、沙箱管理 API |
| 租户 API | 每租户 API Key | 业务后端 | `/api/v1/tenant/volumes*` |
| Kubernetes | kubeconfig / ServiceAccount | 集群运维、VolumeManager | 扫描（get/list）、标签写入（patch）、删除保护（VAP） |

两条应用侧边界必须记住：

- 租户 Key 调管理 API → **401**。
- 控制台 JWT 调租户 API → **401**。
- 管理 API 不存在共享的 `AdminApiKey` 后门。

### 2.2 租户

- 一个租户代表一个业务（例如 `goodcrew`），包含：
  - `id`：租户 slug，必须匹配 `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`，最长 63 字符，创建后不可修改；
  - 显示名；
  - 一个或多个命名空间；
  - 状态：`active` / `disabled`。
- **一个命名空间只能分配给一个租户**，在 `tenant_namespaces` 中全局唯一。
- 租户可以签发多个 API Key，用于轮换与多环境。
- 租户被停用（`disabled`）后，其所有 Key 立即无法通过鉴权。
- 控制台角色是**平台级**的，不是租户级：有 `volume:viewer` 的人可以看到所有租户的卷。租户隔离只在租户 API 内生效。

### 2.3 台账、观测与审计

| 表 | 角色 | 生命周期 |
| --- | --- | --- |
| `volumes` | **归属权威**：租户、owner、期望 lifecycle、台账状态 | 行永不物理删除；消失后保留墓碑 |
| `cluster_volumes` | **当前观测快照**：由最近一轮扫描写入 | 只保留当前存在的 PVC；扫描成功后删除已不存在的行 |
| `managed_namespaces` | 受管命名空间的发现状态 | `active` / `gone` / `unmanaged` |
| `scan_runs` | 每轮扫描的统计与错误 | 追加记录 |
| `volume_events` | append-only 审计事件 | 只追加，无修改/删除 API |

两个重要语义：

- **名字不是身份。** 台账以 `(namespace, claimName)` 关联观测，但同名 PVC 可能被删除后重建；重建会产生新 UID。此时原台账保持 `missing`，并在详情/报告中标记 `reappeared`，直到租户重新注册更新 `RegisteredUid`。
- **台账的 lifecycle 是权威期望值，PVC 标签是执行状态。** 标签被外部改动属于漂移，单独记事件；但删除保护只看 PVC 上的实际标签（见 §2.8）。

### 2.4 标签契约

命名空间：

| 位置 | 键 | 值 | 作用 |
| --- | --- | --- | --- |
| Namespace | `opensandbox.io/volume-managed` | `"true"` | 声明此命名空间受管；扫描与 VAP Binding 都依赖它 |

PVC：

| 键 | 值 | 写入时机 | 作用 |
| --- | --- | --- | --- |
| `opensandbox.io/volume-lifecycle` | `business` \| `ephemeral` | 注册 / lifecycle 变更 | **唯一参与删除保护判定的标签** |
| `opensandbox.io/volume-tenant` | 租户 slug | 注册 | 归属与对账 |
| `opensandbox.io/volume-owner` | `<ownerKind>-<ownerId>` | 注册 | 归因；非法或超过 63 字符时省略 |
| `opensandbox.io/volume-contract-version` | `1` | 注册 | 契约版本 |

说明：

- owner 标签会做规范化：把 `[^A-Za-z0-9_.-]` 替换为 `-`；如果结果不以字母数字开头/结尾或超过 63 字符，则**省略标签**，台账中仍保留精确的 `ownerKind`/`ownerId`。
- 标签由 VolumeManager 用 merge patch 写入，只触碰上述四个键，不删除业务自己的标签。
- 业务**不要手工写这些标签**。注册 API 是入口；手工写标签会导致台账与标签不一致（漂移）。
- 一个已注册卷的典型标签：

```yaml
metadata:
  labels:
    opensandbox.io/volume-lifecycle: "business"
    opensandbox.io/volume-tenant: "demo"
    opensandbox.io/volume-owner: "employee-demo-1"
    opensandbox.io/volume-contract-version: "1"
```

### 2.5 列表分类

`GET /api/v1/volumes` 返回的是台账 LEFT JOIN 观测的合并视图，分类按以下优先级派生：

| classification | 控制台显示 | 含义 | 建议动作 |
| --- | --- | --- | --- |
| `registered` | 已登记 | 台账存在、观测存在、无违规 | 正常 |
| `unlabeled` | 未标注 | 无台账、PVC 无 lifecycle 标签 | 回填导入或租户注册 |
| `unregistered` | 未登记 | 无台账、PVC 有 lifecycle 标签 | 补登记归属 |
| `drift` | 标签漂移 | 台账存在但 PVC 标签与台账不一致 | 查详情中的违规项并修正 |
| `missing` | 缺失 | 台账存在、PVC 已不在观测中 | 按原因区分正常回收 / 意外消失 |
| `reappeared` | 新 UID 重现 | 同名 PVC 以新 UID 出现，台账仍是旧归属 | 租户重新注册 |
| `unmanaged` | 命名空间失管 | 命名空间失去了 `volume-managed=true` | 重新打标或确认退出治理 |

报告页的计数口径与列表一致：`observed` 是观测行数；`registered` / `unlabeled` / `unregistered` / `drift` / `missing` / `reappeared` / `unmanaged` 是分类计数。注意 `unlabeled` 与 `unregistered` 是读取时派生值，不会写入台账。

### 2.6 台账状态机

状态只有三个：`active`、`missing`、`unmanaged`。

| 当前状态 | 条件 | 新状态 / 原因 | 事件 |
| --- | --- | --- | --- |
| `active` | 命名空间扫描成功，PVC 仍在 | `active` | 更新 `LastSeenAt`，重算违规 |
| `active` | 命名空间扫描失败 | 不动 | 扫描错误记录到 `scan_runs` |
| `active` | PVC 不存在，且从未被观测到 | `missing` / `never-observed` | `missing-before-observed`（高优先级） |
| `active` | PVC 不存在，台账 lifecycle 为 `ephemeral` | `missing` / `ephemeral-gone` | `disappeared`（正常回收） |
| `active` | PVC 不存在，台账 lifecycle 为 `business` | `missing` / `unexpected` | `unexpected-disappearance`（高优先级） |
| `active` | 命名空间被删除（404） | `missing` / `namespace-deleted` | `namespace-mass-disappearance` |
| `active` | 命名空间存在但失去受管标签 | `unmanaged` | `namespace-unmanaged` |
| `missing` | PVC 以相同 UID 出现 | `active` | `reactivated`（系统纠正） |
| `missing` | PVC 以新 UID 出现 | 保持 `missing` | `reappeared-with-new-uid`，等重新注册 |
| `missing` | 租户重新注册且 PVC 存在 | `active`，更新 `RegisteredUid` | `reactivated` |
| `unmanaged` | 命名空间重新受管且 PVC 存在 | `active` | `namespace-managed-again` |

**只有正面证据才判消失**：只有命名空间本轮 list 成功、且返回清单中确实没有该 PVC，才允许标记 `missing`。list 失败时该命名空间本轮不做任何状态迁移。这是为了避免一次 API 抖动把全集群卷误判为消失。

### 2.7 lifecycle：业务卷与临时卷

| lifecycle | 含义 | 删除保护 |
| --- | --- | --- |
| `business` | 承载业务持久数据，删除需授权 | 受保护 |
| `ephemeral` | 业务声明可自行回收的临时卷 | 不受保护 |

- 注册时可以指定 lifecycle，但**已存在卷的 lifecycle 变更只能走专用端点**；用注册 API 提交不同 lifecycle 会返回 `conflict`，以避免绕过审计。
- `business → ephemeral` 必须提供 `reason`（≤ 512 字符），写入 `lifecycle-changed` 事件。
- `ephemeral → business` 不需要 reason。
- 提交与当前相同的 lifecycle 返回 `unchanged`。
- lifecycle 先写台账，再 patch PVC 标签。patch 失败时响应为 `label-pending`，台账中的 lifecycle 已更新，但 PVC 标签仍是旧值；下一轮扫描会重试标签写入。**在标签真正变成 `ephemeral` 之前，VAP 仍会拦截删除。**

### 2.8 删除保护语义

保护由一个 `ValidatingAdmissionPolicy` + Binding 执行，位于 Kubernetes apiserver 的 DELETE 准入阶段：

```text
拒绝删除的条件：
  PVC 的现有标签中存在 opensandbox.io/volume-lifecycle
  且该值不等于 "ephemeral"
  且请求者不属于 opensandbox-volume-approvers 组
```

DELETE 准入请求没有 `object`，策略读取的是 `oldObject.metadata.labels`，即被删除 PVC 的现有标签。

具体结论：

| PVC 上的 lifecycle 标签 | 是否保护 |
| --- | --- |
| 不存在 | **否**（fail-open，只进报告，驱动回填） |
| `ephemeral` | 否 |
| `business` | 是 |
| 其他值（拼错、大小写不符、未知值） | **是**（fail-closed，按 business 处理） |

其他要点：

- **命名空间必须带 `opensandbox.io/volume-managed: "true"`**，Binding 才会在该命名空间生效。未打标的命名空间即使 PVC 有 business 标签也不会被拦截。
- 审批组固定为 `opensandbox-volume-approvers`；业务租户不应加入。
- `system:masters` **不自动豁免**。
- 组件可以宕机：保护在 apiserver 内执行，不依赖 VolumeManager 可用性。
- P2 阶段先以 `Warn + Audit` 部署，观察审计日志；P3 才改成 `Deny`。默认清单是 P2 状态。
- `Deny` 生效后，受管命名空间中的 `deletecollection` / 按 selector 的批量删除是 **fail-closed**：集合删除不携带 old object，CEL 无法批准，因此会被拒绝。运维删除请逐个删除，或使用审批组身份。
- **删除命名空间会级联删除 PVC，不经过 PVC 的 admission**；平台通过批量消失检测与报告记录，而不是拦截。
- **PV 不受 PVC 的 VAP 保护**；存储侧应使用 `reclaimPolicy: Retain` 并限制 PV 删除权限。
- 逃生通道：移除 `opensandbox.io/volume-lifecycle` 标签（会留下审计日志），或由审批组删除。
- VolumeManager 的默认 RBAC 没有 `delete`，其 ServiceAccount 无法删除 PVC。

### 2.9 控制台角色

| 角色 | 读取 | 写入 |
| --- | --- | --- |
| `volume:viewer` | 卷列表、卷详情、报告、租户与 Key 列表 | 无 |
| `volume:admin` | 同 viewer | 扫描、导入、租户新建/编辑、Key 签发/吊销、lifecycle 变更 |
| `sandbox:viewer` | 沙箱列表、详情、指标 | 无 |
| `sandbox:admin` | 同 sandbox:viewer | 创建、暂停、恢复、续期、删除沙箱；获取数据面端点 |

- 同分面 `admin` 隐含 `viewer`；`sandbox` 与 `volume` 两个分面互不隐含。
- 无任何角色的账号可以完成 Keycloak 登录，但只能看到「没有控制台访问权限」页。
- 角色只从 `resource_access.<Auth:ClientId>.roles` 读取；不读 `realm_access`，也不读其他客户端的角色。

---

## 3. 快速起步

### 3.1 选择路径

| 路径 | 适用场景 | 需要什么 | 体验范围 |
| --- | --- | --- | --- |
| A. Docker Compose | 先看控制台与页面 | Docker | 可以登录、浏览页面；**没有 Kubernetes API，无法扫描真实 PVC** |
| B. 本地开发 + 真实集群 | 开发/联调/演示完整能力 | Docker、.NET 10、Node.js、K8s 集群 | 扫描、注册、标签、报告、降级全部可用 |
| C. 集群内部署 | 生产环境 | K8s ≥ 1.30、PostgreSQL、Keycloak、镜像 | 完整能力 + 删除保护 |

Kubernetes 版本要求：删除保护的 `ValidatingAdmissionPolicy` 在 **Kubernetes ≥ 1.30** 为 GA；1.28–1.29 需要特性门控；低于 1.28 需要改用 Webhook（本仓库未提供）。

### 3.2 路径 A：Docker Compose 看控制台

```bash
cp .env.example .env
# 至少修改 KEYCLOAK_ADMIN_PASSWORD、KEYCLOAK_DB_PASSWORD；
# POSTGRES_PASSWORD 如修改，只能使用字母、数字、- _ . ~，不要包含 ; 引号或空白。
docker compose up -d --build
docker compose ps
```

打开：

| 服务 | 地址 |
| --- | --- |
| 控制台 | <http://localhost:5080> |
| Keycloak 管理控制台 | <http://localhost:8081> |
| PostgreSQL | `127.0.0.1:5432` |

内置 realm 提供的本地开发用户，密码均为 `change-me-local`（仅限本地开发）：

| 用户 | 角色 |
| --- | --- |
| `volume-admin` | `volume:admin` |
| `volume-viewer` | `volume:viewer` |
| `sandbox-admin` | `sandbox:admin` |
| `sandbox-viewer` | `sandbox:viewer` |

用 `volume-admin` 登录后可以看到「卷管理」菜单：卷列表、租户管理、违规报告、回填导入。

Compose 的注意事项：

- `opensandbox-web` 容器内没有 Kubernetes API（没有 in-cluster ServiceAccount，也没有挂载 kubeconfig）。`VOLUME_MANAGER_ENABLED=true` 只用于首次启动执行 EF 迁移；之后扫描器会每轮失败并在日志中报错。
- 首次启动必须保持 `VOLUME_MANAGER_ENABLED=true`（或对同一连接串运行 `dotnet ef database update` / 迁移 Job），否则全新数据库没有表，卷接口返回 `503 dependency_unavailable`。
- 确认表已建好后，可以把 `.env` 中的 `VOLUME_MANAGER_ENABLED` 改为 `false`，再执行 `docker compose up -d opensandbox-web`，停止扫描噪声；租户注册与只读路径仍然可用。
- 想在 Compose 里看到真实 PVC，请使用路径 B 或给容器挂载可用的 kubeconfig（本仓库默认不这样做）。

### 3.3 路径 B：本地开发 + 真实集群（推荐）

前置条件：

- Kubernetes 集群（建议 kind / 测试集群），`kubectl` 当前 context 可 `get/list` PVC 和 Pod，且可以 `patch` PVC 标签；
- Docker（运行 PostgreSQL 与 Keycloak）；
- .NET SDK 10；
- Node.js 20.19+ 或 22.12+。

#### 第 1 步：准备命名空间

```bash
kubectl create namespace opensandbox --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace opensandbox opensandbox.io/volume-managed=true --overwrite
kubectl get namespaces -l opensandbox.io/volume-managed=true
```

本地后端使用你的 kubeconfig 身份访问集群，所以这一步的标签是必须的，RBAC 不是必须的。若要预演集群内身份，再执行：

```bash
kubectl apply -f deploy/volume-manager/rbac.yaml
```

#### 第 2 步：启动 PostgreSQL 与 Keycloak

```bash
cp .env.example .env
docker compose --env-file .env up -d postgres keycloak-postgres keycloak
```

Keycloak 首次启动会导入 `deploy/keycloak/opensandbox-realm.json`；控制台开发登录使用 `http://localhost:8081/realms/opensandbox`。

#### 第 3 步：启动后端

Linux / macOS / Git Bash：

```bash
export ConnectionStrings__VolumeManager="Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=opensandbox"
export VolumeManager__Enabled=true
export VolumeManager__KubeConfigPath="$HOME/.kube/config"
dotnet run --project backend
```

Windows PowerShell（`$HOME` 是 Windows 路径）：

```powershell
$env:ConnectionStrings__VolumeManager = "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=opensandbox"
$env:VolumeManager__Enabled = "true"
$env:VolumeManager__KubeConfigPath = "$HOME\.kube\config"
dotnet run --project backend
```

说明：

- 如果修改了 `.env` 的 `POSTGRES_PASSWORD`，上面的连接串密码也要同步。
- `dotnet run` 默认使用 `backend/appsettings.Development.json`，其中 `Auth:Authority` 已指向本地 Keycloak，`RequireHttpsMetadata=false`。
- 后端启动时执行 EF 迁移（`Enabled && MigrateOnStartup`）。
- 若集群 API 不可达，`/readyz` 仍然可用（数据库可达即可）；扫描会记录错误，台账与报告仍可读。

#### 第 4 步：启动前端开发服务器

```bash
cd frontend
npm install
cp .env.example .env.local
npm run dev
```

打开 <http://localhost:3000>，用 `volume-admin` / `change-me-local` 登录。Vite 会把 `/api` 代理到 `http://localhost:5080`。

#### 第 5 步：创建测试 PVC 并扫描

```yaml
# demo-pvc.yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: demo-workspace
  namespace: opensandbox
spec:
  accessModes: ["ReadWriteOnce"]
  resources:
    requests:
      storage: 1Gi
```

```bash
kubectl apply -f demo-pvc.yaml
```

PVC 在未挂载时可能显示 `Pending`，这不影响扫描。打开控制台「卷列表」，点击「立即扫描」，应能看到 `opensandbox/demo-workspace`，分类为「未标注」。

#### 第 6 步：创建租户、签发 Key、注册卷

1. 控制台「租户管理」→「新建租户」：
   - 租户 ID：`demo`
   - 显示名：`Demo`
   - 命名空间：`opensandbox`
2. 在租户行点击「Key」→ 签发 Key。**明文只显示一次**，立即保存。
3. 用 Key 调用注册接口（下面的 `$TENANT_KEY` 换成刚保存的 Key）：

```bash
export TENANT_KEY="<签发的租户 Key>"

curl -sS -X POST "http://localhost:5080/api/v1/tenant/volumes/register" \
  -H "Authorization: Bearer $TENANT_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "namespace": "opensandbox",
    "volumes": [{
      "claimName": "demo-workspace",
      "ownerKind": "employee",
      "ownerId": "demo-1",
      "role": "meta",
      "mountPath": "/workspace",
      "lifecycle": "business",
      "sandboxId": "sbx-demo"
    }]
  }'
```

期望返回：

```json
{
  "results": [
    { "claimName": "demo-workspace", "status": "registered", "volumeId": "..." }
  ]
}
```

验证标签：

```bash
kubectl get pvc demo-workspace -n opensandbox \
  -o jsonpath='{.metadata.labels}'; echo
```

应看到 `opensandbox.io/volume-lifecycle=business`、`opensandbox.io/volume-tenant=demo` 等标签。刷新卷列表，该卷变为「已登记 / 业务持久」。

#### 第 7 步：体验降级

```bash
curl -sS -X POST \
  "http://localhost:5080/api/v1/tenant/volumes/opensandbox/demo-workspace/lifecycle" \
  -H "Authorization: Bearer $TENANT_KEY" \
  -H "Content-Type: application/json" \
  -d '{"lifecycle":"ephemeral","reason":"demo 环境清理"}'
```

期望：

```json
{ "status": "updated", "labelState": "applied" }
```

PVC 的 lifecycle 标签变成 `ephemeral`。此时如果集群已部署 VAP 且 Binding 为 `Deny`，删除会被放行；如果仍是 `Warn + Audit`，删除会成功但审计日志中会有告警。

删除保护本身需要单独部署，见 §4.5 与[运维手册](volume-manager-operations.md#9-删除保护)。

### 3.4 路径 C：集群内部署

本仓库提供 RBAC、VAP 与命名空间打标示例，但不提供后端 Deployment 清单。集群内部署至少需要：

1. **PostgreSQL**：外部实例或 StatefulSet；连接串通过 Secret 注入，例如 `ConnectionStrings__VolumeManager`。
2. **Keycloak**：生产 realm、public client `opensandbox-web`、四个客户端角色；按[Keycloak 客户端配置](../deploy/volume-manager/keycloak-client.md)登记控制台源、redirect URI、post logout redirect URI 与 web origin。
3. **后端 Deployment**：
   - `serviceAccountName: opensandbox-volume-manager`（与 `rbac.yaml` 中的 ServiceAccount 同命名空间）；
   - 环境变量 `VolumeManager__Enabled=true`、`ConnectionStrings__VolumeManager`、`Auth__Authority`、`Auth__ClientId`；
   - 首次启动执行迁移（`MigrateOnStartup=true`），或改用独立迁移 Job 后关闭；
   - 探针使用 `/healthz`（存活）与 `/readyz`（数据库就绪）。
4. **RBAC 与命名空间**：

   ```bash
   kubectl apply -f deploy/volume-manager/rbac.yaml
   kubectl label namespace opensandbox opensandbox.io/volume-managed=true
   ```

   > `rbac.yaml` 假定后端与 ServiceAccount 位于 `opensandbox` 命名空间。部署到其他命名空间时，需同步修改 YAML 中的 `namespace`、ClusterRoleBinding 的 subject 以及 Deployment 的 `serviceAccountName`。

5. **删除保护**：按运维手册的 P2（`Warn + Audit`）→ P3（`Deny`）顺序执行，不要跳过观察期。
6. **不要**给 VolumeManager 的 ServiceAccount 授予 `delete`；它不需要，也不应该拥有。

上线顺序（P0 → P3）与每一步的验收标准见[运维手册](volume-manager-operations.md#2-上线顺序)。

### 3.5 首次启动验收清单

- [ ] 匿名 `GET /api/v1/volumes` 返回 401。
- [ ] `volume-viewer` 能看卷列表/详情/报告，但看不到「立即扫描」「降级/升级」「回填导入」。
- [ ] `volume-admin` 能触发扫描、管理租户与 Key、变更 lifecycle。
- [ ] 扫描后卷列表出现命名空间中的 PVC；未标注卷被列入报告。
- [ ] 新建租户并签发 Key；用 Key 注册卷成功，PVC 出现四个契约标签。
- [ ] 降级为 `ephemeral` 后标签更新；若已启用 VAP `Deny`，该 PVC 可被删除。
- [ ] 在测试集群验证 VAP：业务卷删除被拒，审批组可删除，降级后可删除。
- [ ] `/readyz` 在数据库可达时返回成功；Kubernetes API 不可用不使其失败。

---

## 4. 使用指南

### 4.1 控制台

路由与角色：

| 路由 | 页面 | 访问角色 | 主要操作 |
| --- | --- | --- | --- |
| `/volumes` | 卷列表 | `volume:viewer` / `volume:admin` | 筛选、搜索、排序、分页、查看详情；admin 可立即扫描、降级/升级 |
| `/volumes/:namespace/:claimName` | 卷详情 | 同上 | 台账、集群观测、原始标签、事件时间线 |
| `/volume-tenants` | 租户管理 | 同上 | 租户列表与统计；admin 可新建/编辑/停用、签发/吊销 Key |
| `/volume-report` | 违规报告 | 同上 | 分类统计、覆盖率、租户/命名空间分布、违规与扫描错误；admin 可立即扫描 |
| `/volume-import` | 回填导入 | `volume:admin` | CSV 解析 → 预览 → Dry run → 确认写入 |

#### 卷列表

- 支持按命名空间、租户 ID、分类、lifecycle、是否挂载筛选，以及按 PVC/owner/租户子串搜索；筛选条件同步到 URL，可分享或刷新保持。
- 分页在服务端完成，`pageSize` 默认 20、最大 200；排序字段为 `namespace`、`claimName`、`createdAt`、`lastSeenAt`、`tenant`。
- 顶部展示最后扫描时间、标签覆盖率、登记覆盖率。
- `volume:admin` 可点「立即扫描」；同一时间只允许一轮扫描，已有扫描进行中会返回 `409 scan_in_progress`。
- 行操作的「降级/升级」只对已有台账行的卷显示，并弹出确认框；降级必须填写原因。

#### 卷详情

- 「台账」卡片：租户、owner、role、挂载路径、台账状态与原因、标签状态、登记 UID、观测 UID、登记/最后观测时间。
- 「集群观测」卡片：phase、StorageClass、容量、是否挂载、挂载 Pod、K8s 创建时间、删除时间戳、原始标签。
- 「违规」提示：当前违规集合。
- 「事件时间线」：最近 100 条 `volume_events`，按时间倒序；用于解释注册、标签写入、漂移、消失与重新出现。

#### 租户管理

- 列表展示租户状态、命名空间、卷/业务卷/临时卷/缺失数量与有效 Key 数量。
- 新建租户时，租户 ID 必须符合小写 DNS 标签规则，且创建后不可修改；命名空间不能已分配给其他租户。
- 「Key」弹窗列出 Key 名称、前缀、创建/吊销/最后使用时间；`volume:admin` 可签发和吊销。**签发响应中的明文 Key 只显示一次**。
- 停用租户后，该租户的所有 Key 立即失效；重新启用后恢复（Key 未被吊销的前提下）。

#### 违规报告

- 统计卡：观测、已登记、未标注、未登记、漂移、缺失、新 UID 重现、失管。
- 覆盖率：标签覆盖率 = 有 lifecycle 标签的观测卷 / 观测卷；登记覆盖率 = 有台账的观测卷 / 观测卷。
- 分布：按租户、按命名空间。
- 违规清单：未标注、未登记、漂移、意外消失、注册后从未观测即消失、命名空间失管、同名新 UID 重现、未知租户标签。列表超过 200 条时截断，`truncated=true`，完整数据用卷列表筛选。
- 最后扫描的触发方式、命名空间成功数/总数、逐命名空间错误。

#### 回填导入

流程固定为：

```text
CSV / 粘贴 → 解析预览 → Dry run → 确认写入
```

- 默认 dry-run；dry-run 不写数据库、不 patch 标签。
- 写入前必须基于当前 CSV 文本重新执行一次成功的 dry-run；修改文本会使旧结果失效。
- CSV 表头固定：

```csv
tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId
```

- 导入不会覆盖其他租户或其他 owner 的归属；冲突逐条报告，不中止整批。
- 写入后标签 patch 失败的条目返回 `label-pending`，台账保留，下一轮扫描重试。

### 4.2 租户接入（业务后端）

接入分五步：

```text
平台管理员创建租户 + 命名空间
        ↓
签发租户 API Key（明文只显示一次）
        ↓
业务后端调用注册 API（PVC 已存在）
        ↓
平台写台账 + patch PVC 标签
        ↓
业务查询自己的卷 / 需要时降级 lifecycle
```

#### 4.2.1 注册卷

```http
POST /api/v1/tenant/volumes/register
Authorization: Bearer <tenant-key>
Content-Type: application/json
```

```json
{
  "namespace": "opensandbox",
  "volumes": [
    {
      "claimName": "trainer-ws-3f2a9c1b7d4e-meta",
      "ownerKind": "employee",
      "ownerId": "3f2a9c1b7d4e",
      "role": "meta",
      "mountPath": "/workspace",
      "lifecycle": "business",
      "sandboxId": "sbx-..."
    }
  ]
}
```

响应为 HTTP 200，允许部分成功：

```json
{
  "results": [
    { "claimName": "trainer-ws-3f2a9c1b7d4e-meta", "status": "registered", "volumeId": "..." }
  ]
}
```

`status` 取值：

| status | 含义 | 调用方动作 |
| --- | --- | --- |
| `registered` | 新建台账并写标签 | 成功 |
| `updated` | 同租户同 owner、lifecycle 一致；更新可变字段并重试标签 | 成功 |
| `reactivated` | 台账为 `missing`，PVC 已重建；更新 UID 并回到 `active` | 成功 |
| `conflict` | 归属或 lifecycle 冲突；未写任何标签 | 不要重试，人工核对 |
| `not-found` | PVC 不存在 | 退避后重试 |
| `invalid` | 命名空间未分配给该租户、未打受管标签或字段非法 | 修正配置/参数 |
| `label-pending` | 台账已写入，但 PVC 标签 patch 失败 | 无需重复注册；下一轮扫描重试 |

注册语义：

- **幂等**：同租户同 owner 重复注册不会产生第二行台账，只更新 `role`、`mountPath`、`sandboxId` 等可变字段。
- **归属不可变**：`ownerKind` / `ownerId` 不允许被任何一方改写；不同租户或不同 owner 注册同一 PVC 返回 `conflict`。
- **owner 跨命名空间唯一**：同一租户的某个 owner 已绑定另一个 PVC 时，新注册返回 `conflict`。
- **lifecycle 不可通过注册改写**：已存在台账且 lifecycle 不同，返回 `conflict`；请走 lifecycle 端点。
- **命名空间必须同时满足**：已分配给该租户、带 `opensandbox.io/volume-managed=true` 标签。
- **PVC 必须已存在**：OpenSandbox 创建 PVC 是同步的；若尚未出现，按指数退避重试 `not-found`。
- 注册只写台账与标签，不删除、不回收任何卷。

#### 4.2.2 查询自己的卷

```http
GET /api/v1/tenant/volumes?page=1&pageSize=20
Authorization: Bearer <tenant-key>
```

支持与管理列表相同的筛选/搜索/排序参数，但服务端强制只返回该租户的卷：自己的台账行，以及标签租户为该租户、且命名空间已分配给该租户的观测行；其他观测行不可见。

#### 4.2.3 变更 lifecycle

```http
POST /api/v1/tenant/volumes/{namespace}/{claimName}/lifecycle
Authorization: Bearer <tenant-key>
Content-Type: application/json
```

```json
{ "lifecycle": "ephemeral", "reason": "业务确认工作区不再保留" }
```

响应：

```json
{ "status": "updated", "labelState": "applied" }
```

- `business → ephemeral` 必须提供 `reason`，否则 400；`reason` 上限 512 字符。
- 降级成功后标签变为 `ephemeral`，VAP 才会放行删除。
- 若返回 `label-pending`，台账已经是 `ephemeral`，但 PVC 标签尚未更新；在标签更新前删除仍会被 VAP 拒绝。下一轮扫描重试，详情页事件时间线可确认结果。
- 降级不会替你删除 PVC；删除由业务按自己的回收策略执行。

#### 4.2.4 Key 轮换

1. 在控制台为租户签发新 Key；
2. 业务切换到新 Key；
3. 确认旧 Key 的 `LastUsedAt` 不再变化（最多 5 分钟更新一次）；
4. 吊销旧 Key。

吊销立即生效；库中只保存 Key 的 SHA-256 哈希与前 8 字符前缀，明文只在签发响应中出现一次。

#### 4.2.5 错误处理

| HTTP | code | 含义 |
| --- | --- | --- |
| 400 | `invalid_argument` | 字段非法、降级缺 reason、批量请求为空 |
| 401 | `unauthorized` | Key 缺失/无效/已吊销，或租户已停用 |
| 404 | `not_found` | 卷不存在或不属于该租户（不泄露其他租户信息） |
| 409 | `conflict` | 租户/namespace 冲突等 |
| 503 | `dependency_unavailable` | Kubernetes 或数据库暂不可用；应退避重试 |

批量注册本身返回 200 + 逐条状态，不要用整体 HTTP 状态判断单条结果。

### 4.3 管理 API

管理 API 面向控制台；所有请求都需要 Keycloak JWT：

```http
Authorization: Bearer <JWT>
```

鉴权规则：

- 未登录/无 token → 401；
- 已登录但缺少对应角色 → 403；
- 角色来源只读 `resource_access.<Auth:ClientId>.roles`；
- GET/HEAD 需要 `volume:viewer`（或 `volume:admin`）；写操作需要 `volume:admin`。

本地调试时，可以从浏览器 DevTools → **Application → Session Storage → 控制台源** 读取 `osb_at`；该 token 生命周期很短（Keycloak 建议 5 分钟），不要写到脚本或日志里。生产自动化应通过标准 OIDC 授权码流程获取 token，不应使用租户 Key。

#### 4.3.1 卷列表

```bash
export JWT="<控制台 access token>"

curl -sS "http://localhost:5080/api/v1/volumes?page=1&pageSize=20&classification=unlabeled" \
  -H "Authorization: Bearer $JWT" | jq .
```

查询参数：

| 参数 | 说明 |
| --- | --- |
| `page` | 页码，默认 1 |
| `pageSize` | 每页条数，默认 20，上限 200 |
| `namespace` | 命名空间精确匹配 |
| `tenant` | 租户 ID 精确匹配 |
| `classification` | `registered` / `unlabeled` / `unregistered` / `drift` / `missing` / `reappeared` / `unmanaged` |
| `lifecycle` | `business` / `ephemeral` |
| `mounted` | `true` / `false` |
| `q` | 按 claimName、owner、tenant、tenantName 子串搜索，大小写不敏感 |
| `sort` | `namespace` / `claimName` / `createdAt` / `lastSeenAt` / `tenant` |
| `order` | `asc` / `desc` |

语义：筛选 → 搜索 → 排序 → 最后切片；`total` 是筛选后的全量；页码越界返回空页；依赖不可用返回 503，不返回空清单。

#### 4.3.2 卷详情与报告

```bash
curl -sS "http://localhost:5080/api/v1/volumes/opensandbox/demo-workspace" \
  -H "Authorization: Bearer $JWT" | jq .

curl -sS "http://localhost:5080/api/v1/volumes/report" \
  -H "Authorization: Bearer $JWT" | jq .
```

详情响应包含 `volume`（合并视图）、`observed`（集群观测，可能为 null）和 `events`（最近 100 条事件）。报告包含最近扫描、分类计数、覆盖率、租户/命名空间分布与截断后的违规清单。

#### 4.3.3 触发扫描

```bash
curl -sS -X POST "http://localhost:5080/api/v1/volumes/scan" \
  -H "Authorization: Bearer $JWT" | jq .
```

- 同步执行；响应包含 `scanId`、触发方式、起止时间、命名空间计数、卷数量与逐命名空间错误。
- 已有扫描进行中 → `409 scan_in_progress`。

#### 4.3.4 租户与 Key

```bash
# 新建租户
curl -sS -X POST "http://localhost:5080/api/v1/volume-tenants" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"id":"demo","displayName":"Demo","namespaces":["opensandbox"]}' | jq .

# 修改显示名、状态或命名空间
curl -sS -X PATCH "http://localhost:5080/api/v1/volume-tenants/demo" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"status":"disabled"}' | jq .

# 签发 Key（明文只返回一次）
curl -sS -X POST "http://localhost:5080/api/v1/volume-tenants/demo/keys" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"name":"prod"}' | jq .

# 吊销 Key（204 No Content）
curl -sS -X POST "http://localhost:5080/api/v1/volume-tenants/demo/keys/<keyId>/revoke" \
  -H "Authorization: Bearer $JWT" -o /dev/null -w '%{http_code}\n'
```

#### 4.3.5 管理员变更 lifecycle

```bash
curl -sS -X POST \
  "http://localhost:5080/api/v1/volumes/opensandbox/demo-workspace/lifecycle" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"lifecycle":"ephemeral","reason":"管理员确认回收"}' | jq .
```

管理员也必须为降级提供 reason；审计事件中的 actor 为 `admin`。

#### 4.3.6 回填导入

```bash
curl -sS -X POST "http://localhost:5080/api/v1/volumes/import" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{
    "dryRun": true,
    "items": [{
      "tenant": "demo",
      "namespace": "opensandbox",
      "claimName": "demo-workspace",
      "ownerKind": "employee",
      "ownerId": "demo-1",
      "role": "meta",
      "mountPath": "/workspace",
      "lifecycle": "business",
      "sandboxId": "sbx-demo"
    }]
  }' | jq .
```

- `dryRun` 默认 `true`；只有在明确传 `false` 时才写库与写标签。
- dry-run 的逐条状态为 `would-register` / `would-update`，以及冲突/无效/不存在状态。
- 写入模式状态与注册 API 同构，另加 `registered` / `updated` / `reactivated` / `label-pending`。
- 同名同租户同 owner 且 lifecycle 不一致 → `conflict`，不覆盖。
- 导入不会为映射之外的卷打标签或建台账。

#### 4.3.7 错误与限制

| HTTP | code | 含义 |
| --- | --- | --- |
| 400 | `invalid_argument` | 参数或字段非法 |
| 401 | `unauthorized` | 未登录、token 无效 |
| 403 | `forbidden` | 角色不足；响应可能带 `roles` |
| 404 | `not_found` | 卷/租户/Key 不存在 |
| 409 | `conflict` | 租户或 namespace 冲突 |
| 409 | `scan_in_progress` | 已有扫描进行中 |
| 503 | `dependency_unavailable` | 数据库/Kubernetes 不可用 |

管理 API **没有删除端点**；任何声称能通过本服务删除 PVC 的方式都不存在于设计内。

### 4.4 回填导入的判定规则

| 场景 | 结果 |
| --- | --- |
| 无台账，PVC 存在，字段合法，命名空间已分配且受管 | `registered` / dry-run `would-register` |
| 同租户同 owner，lifecycle 一致 | `updated` / dry-run `would-update` |
| 台账为 `missing` 或 `unmanaged`，PVC 存在 | 重新激活并应用请求的 lifecycle |
| 台账已存在但租户/owner/lifecycle 不一致 | `conflict` |
| owner 已绑定该租户的其他 PVC | `conflict` |
| PVC 不存在 | `not-found` |
| 租户不存在/停用、命名空间未分配/未受管、字段非法、同批次重复项 | `invalid` |
| 写入后标签 patch 失败 | 台账保留，状态 `label-pending` |

### 4.5 删除与回收流程

**正常业务回收：**

```text
业务调用 lifecycle 端点降级为 ephemeral（带 reason）
        ↓
VolumeManager 将 PVC 标签改为 ephemeral
        ↓
VAP 放行删除
        ↓
业务按自己的回收策略删除 PVC
        ↓
下一轮扫描发现 PVC 不存在，台账 → missing（ephemeral-gone），事件 disappeared
```

**管理员删除受保护卷：**

- 方式一：加入 `opensandbox-volume-approvers` 组后删除（审计记录保留）；
- 方式二：移除 PVC 的 `opensandbox.io/volume-lifecycle` 标签后删除（逃生通道，会进审计日志）；
- 方式三：按正常流程降级后删除。

不建议直接删除命名空间：它会级联删除 PVC，**不经过 PVC admission**，平台只能在下一轮扫描把它记录为 `namespace-deleted` 批量消失。PV 也不受本策略保护，存储侧应使用 `reclaimPolicy: Retain` 并限制删除权限。

---

## 5. 配置参考

### 5.1 配置项

| 配置键 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `ConnectionStrings:VolumeManager` | `ConnectionStrings__VolumeManager` | 空 | Npgsql 连接串；`Enabled=true` 时必填 |
| `VolumeManager:Enabled` | `VolumeManager__Enabled` | `false` | 是否启动扫描器与启动迁移 |
| `VolumeManager:ScanInterval` | `VolumeManager__ScanInterval` | `00:05:00` | 扫描周期，必须大于零 |
| `VolumeManager:ScanOnStartup` | `VolumeManager__ScanOnStartup` | `true` | 启动时是否立即扫描 |
| `VolumeManager:ManagedNamespaceSelector` | `VolumeManager__ManagedNamespaceSelector` | `opensandbox.io/volume-managed=true` | 发现受管命名空间的选择器 |
| `VolumeManager:MaxConcurrentNamespaces` | `VolumeManager__MaxConcurrentNamespaces` | `4` | 单轮扫描的命名空间并发上限，至少为 1 |
| `VolumeManager:KubeConfigPath` | `VolumeManager__KubeConfigPath` | 空 | 为空时使用 in-cluster 配置 |
| `VolumeManager:MigrateOnStartup` | `VolumeManager__MigrateOnStartup` | `true` | 是否在启动时执行 EF 迁移；仅 `Enabled=true` 时生效 |
| `Auth:Authority` | `Auth__Authority` | 必填 | Keycloak realm URL，浏览器与后端共同使用 |
| `Auth:ClientId` | `Auth__ClientId` | 必填 | 必须是 `opensandbox-web`（或你实际创建的客户端 ID） |
| `Auth:RequireHttpsMetadata` | `Auth__RequireHttpsMetadata` | `true` | 生产必须为 `true` |
| `Auth:MetadataAddress` | `Auth__MetadataAddress` | 空 | 可选；容器内 discovery/JWKS 地址，不暴露给浏览器 |

完整示例：

```json
{
  "ConnectionStrings": {
    "VolumeManager": "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=..."
  },
  "Auth": {
    "Authority": "https://keycloak.example.com/realms/opensandbox",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  },
  "VolumeManager": {
    "Enabled": true,
    "ScanInterval": "00:05:00",
    "ScanOnStartup": true,
    "ManagedNamespaceSelector": "opensandbox.io/volume-managed=true",
    "MaxConcurrentNamespaces": 4,
    "KubeConfigPath": null,
    "MigrateOnStartup": true
  }
}
```

### 5.2 启动迁移与关闭扫描

迁移条件是 `Enabled && MigrateOnStartup`：

- 全新数据库必须先用 `Enabled=true` 启动一次，或对同一连接串执行 `dotnet ef database update` / 独立迁移 Job。
- schema 建好后，没有 Kubernetes API 的环境（例如 Compose）可以设置 `Enabled=false`：
  - 扫描器不启动，不再产生扫描记录；
  - 租户注册、只读列表/报告、租户与 Key 管理仍然可用；
  - 不会更新集群观测数据。
- 面对未迁移的全新数据库直接 `Enabled=false`，卷相关端点会返回 `503 dependency_unavailable`。

### 5.3 数据库连接

- 生产建议通过 Secret 注入连接串。
- Compose 会把 `POSTGRES_PASSWORD` 直接插进 Npgsql 连接串；密码只能包含字母、数字、`-`、`_`、`.`、`~`，不要包含 `;`、引号或空白。`;` 是连接串分隔符，会导致启动迁移失败并崩溃重启。
- 备份与恢复命令见[运维手册 §3](volume-manager-operations.md#3-postgresql)。

### 5.4 健康检查

| 端点 | 用途 |
| --- | --- |
| `/healthz` | 存活检查 |
| `/readyz` | 就绪检查；检查数据库可达，Kubernetes API 不可用**不**使其失败 |
| `/api/v1/health` | 兼容原有脚本的健康端点 |

---

## 6. 数据与审计

### 6.1 表结构速览

| 表 | 关键内容 |
| --- | --- |
| `tenants` | 租户 ID、显示名、状态、创建/更新时间 |
| `tenant_namespaces` | `Namespace` 主键；一个命名空间只能属于一个租户 |
| `tenant_api_keys` | Key 的 SHA-256 哈希、8 字符前缀、创建/吊销/最后使用时间 |
| `volumes` | 台账；`(Namespace, ClaimName)` 唯一；归属、期望 lifecycle、状态、违规、UID |
| `cluster_volumes` | 最近一轮观测；只保留当前存在的 PVC |
| `managed_namespaces` | 受管命名空间发现状态 |
| `scan_runs` | 每轮扫描统计与错误 |
| `volume_events` | append-only 审计；无更新/删除 API |

完整列定义见[设计规范 §6.2](superpowers/specs/2026-10-25-global-volume-manager-design.md)。

### 6.2 事件类型

| 类别 | 事件 |
| --- | --- |
| 注册 | `registered`、`updated`、`reactivated`、`registration-conflict` |
| 标签 | `label-applied`、`label-patch-failed`、`label-corrected` |
| 生命周期 | `lifecycle-changed` |
| 导入 | `imported` |
| 漂移 | `drift-detected`、`drift-resolved` |
| 消失/重现 | `disappeared`、`unexpected-disappearance`、`missing-before-observed`、`namespace-mass-disappearance`、`reappeared-with-new-uid`、`terminating-observed` |
| 命名空间 | `namespace-unmanaged`、`namespace-managed-again` |

台账行永不物理删除；事件只追加。审计归因基于台账事件与观测历史，不消费 Kubernetes audit log（VAP 的审计日志由集群侧保留）。

### 6.3 常用只读查询

EF 迁移创建的是带引号的 PascalCase 列名。常用 SQL：

```sql
select "State", "StateReason", count(*) from volumes group by 1, 2 order by 3 desc;

select "StartedAt", "NamespacesScanned", "NamespacesSucceeded", "VolumeCount", "Errors"
from scan_runs order by "StartedAt" desc limit 10;
```

更完整的报告 SQL、备份与恢复见[运维手册 §7](volume-manager-operations.md#7-扫描与报告)。

---

## 7. 运维与故障排查

### 7.1 日常检查

- 控制台「违规报告」：未标注/未登记数量、覆盖率、最后扫描时间与错误。
- `scan_runs`：扫描是否持续成功；某命名空间是否反复失败。
- VAP 审计日志（P2）：`opensandbox-protect-business-volumes` 的「本会拦截」记录是否符合预期。
- 标签 `label-pending` 的卷：检查 RBAC 的 `patch` 权限与 K8s 连通性。
- 删除保护不依赖组件：组件宕机时 VAP 仍生效；但扫描与注册会暂停。

### 7.2 常见现象

| 现象 | 排查 |
| --- | --- |
| 控制台 401 | `Auth:Authority` / `Auth:ClientId` 是否与 Keycloak 一致；浏览器 sessionStorage 是否有未过期 token |
| 控制台 403 | 用户是否在 `resource_access.opensandbox-web.roles` 中；角色名大小写 |
| 租户 API 401 | Key 是否吊销；租户是否 `disabled`；是否使用 `Bearer <key>` |
| `503 dependency_unavailable` | PostgreSQL 是否可达；schema 是否迁移；K8s API 是否可达 |
| 扫描一直失败 | `scan_runs."Errors"`；ServiceAccount 是否可 list PVC/Pod；命名空间标签 |
| Compose 中扫描反复报错 | Compose 没有 K8s API；先以 `Enabled=true` 完成迁移，再设为 `false` |
| 卷显示 `unmanaged` | 命名空间是否移除受管标签；重新打标后下一轮扫描恢复 |
| 状态 `label-pending` | K8s patch 权限或连通性；下一轮扫描自动重试 |
| 导入全部 `invalid` | 命名空间是否分配给租户；是否打受管标签；租户是否 active |
| 误删无法执行 | P3 已启用；先走降级端点，或由审批组操作；批量删除会被 fail-closed 拒绝 |
| 业务卷仍可删除 | 检查命名空间是否带受管标签、VAP 是否部署、Binding 是否仍为 `Warn + Audit` |

更完整的故障排查请见[运维手册 §10](volume-manager-operations.md#10-故障排查)。

---

## 8. 安全模型

| 控制 | 实现 |
| --- | --- |
| 管理 API 单一入口 | 只接受 Keycloak JWT；无共享管理密钥 |
| JWT 校验 | issuer、有效期（30 秒时钟偏差）、`azp == ClientId` |
| 角色隔离 | 只读 `resource_access.<ClientId>.roles`；分面 admin/viewer；默认拒绝 |
| 租户隔离 | 每租户 API Key；查询强制租户过滤；跨租户注册/降级拒绝 |
| Key 存储 | 32 字节随机数；SHA-256 哈希 + 8 字符前缀；明文一次性返回；可吊销 |
| 组件权限 | RBAC 仅 `namespaces get/list`、PVC `get/list/patch`、Pod `get/list`；无 delete/create/update |
| 删除保护 | apiserver 内 VAP；不依赖组件可用性；非法 lifecycle 值 fail-closed |
| 审计 | `volume_events` append-only；`lifecycle-changed` 含 actor/reason |

注意事项：

- 控制台 token 存在浏览器 `sessionStorage`；XSS 可窃取短时 token。前端不引入不可信脚本，生产应配置严格 CSP。
- Keycloak 不可用时控制台无法登录，但 VAP、扫描与租户 API 不受影响。
- VolumeManager 有 PVC `patch` 权限，理论上可改任意标签；代码只 patch 四个契约键。RBAC 无法限制到标签键级，生产应审查后端权限并限制能修改其镜像/配置的人。
- 控制台角色是平台级，不是租户级；租户自助只能通过租户 API Key。
- 删除命名空间与删除 PV 不受 PVC VAP 保护；用 RBAC 限制这两类操作，PV 使用 `Retain`。

---

## 9. 常见问题

**Q1：为什么没有删除卷的按钮/API？**

这是设计约束：平台无法完整理解业务生命周期，回收决策留在业务侧。VolumeManager 只提供归属、审计与保护。合法删除路径是「降级为 ephemeral → 业务删除」。

**Q2：PVC 明明有标签，为什么显示「未标注」？**

只有 `opensandbox.io/volume-lifecycle` 参与分类与保护。其他三个标签（tenant/owner/contract-version）存在但缺 lifecycle 时，仍归类为未标注。通过注册 API 写入，不要手工拼标签。

**Q3：什么是「未登记」？**

PVC 有 lifecycle 标签，但台账没有对应行。常见于手工打了标签、或旧系统遗留。用回填导入或租户注册补归属。

**Q4：为什么注册成功但删除仍被拒绝？**

检查 `LabelState` 是否为 `applied`。若为 `pending`/`label-pending`，PVC 标签还没有改成 `ephemeral`，VAP 仍按旧标签拦截；下一轮扫描会重试。还要确认命名空间带 `volume-managed=true` 且 Binding 为 `Deny`。

**Q5：为什么卷显示 missing？**

台账存在但最近一轮成功扫描中 PVC 不存在。查看 `StateReason`：

- `ephemeral-gone`：正常回收；
- `unexpected`：业务卷意外消失（高优先级）；
- `never-observed`：注册后从未观测到就消失；
- `namespace-deleted`：命名空间被删除。
如果是同名新 UID 重建，分类为 `reappeared`，租户重新注册即可恢复 active。

**Q6：删除命名空间会被 VAP 拦住吗？**

不会。命名空间删除级联删除 PVC，不经过 PVC admission。平台只能通过批量消失检测记录。生产应限制命名空间删除权限。

**Q7：Compose 启动后卷列表是空的、日志一直报扫描失败，正常吗？**

正常。Compose 环境没有 Kubernetes API。先保持 `VOLUME_MANAGER_ENABLED=true` 完成迁移，再把 `VOLUME_MANAGER_ENABLED` 设为 `false` 停止扫描噪声。要看真实卷，使用本地开发 + kubeconfig（路径 B）或集群内部署。

**Q8：全新数据库为什么返回 503？**

schema 只由 `Enabled && MigrateOnStartup` 触发迁移。先在 `Enabled=true` 下启动一次（或运行迁移 Job / `dotnet ef database update`），再关闭扫描。关闭模块时租户注册与读取路径仍可用，但前提是表已存在。

**Q9：控制台能不能只让某个租户看自己的卷？**

不能。控制台角色是平台级的。租户级隔离通过租户 API Key 的 `/api/v1/tenant/volumes*` 实现；不要把租户 Key 发给平台管理员，也不要用控制台角色代替租户隔离。

**Q10：怎么验证删除保护真的生效？**

在**测试集群**中：给命名空间打受管标签、部署 VAP、将 Binding 改为 `Deny`；用非审批组身份删除一个带 `business` 标签的 PVC，应被拒绝；用审批组身份或先降级后删除，应成功。仓库提供 `deploy/volume-manager/kind-protection-test.sh`。不要在生产跳过 P2 观察期。

---

## 10. 附录

### A. 枚举值速查

| 类别 | 值 |
| --- | --- |
| 注册结果 | `registered`、`updated`、`reactivated`、`conflict`、`not-found`、`invalid`、`label-pending` |
| lifecycle 变更结果 | `updated`、`unchanged`、`label-pending` |
| 导入 dry-run 附加结果 | `would-register`、`would-update` |
| 台账状态 | `active`、`missing`、`unmanaged` |
| 台账状态原因 | `ephemeral-gone`、`unexpected`、`namespace-deleted`、`never-observed` |
| 列表分类 | `registered`、`unlabeled`、`unregistered`、`drift`、`missing`、`reappeared`、`unmanaged` |
| 扫描触发 | `scheduled`、`manual`、`startup` |
| lifecycle | `business`、`ephemeral` |
| 标签状态 | `applied`、`pending` |

### B. 代码与部署文件

| 路径 | 内容 |
| --- | --- |
| [`backend/Contracts/VolumeContracts.cs`](../backend/Contracts/VolumeContracts.cs) | API 请求/响应 DTO |
| [`backend/Host/Web/VolumeAdminEndpoints.cs`](../backend/Host/Web/VolumeAdminEndpoints.cs) | 管理 API |
| [`backend/Host/Web/VolumeTenantEndpoints.cs`](../backend/Host/Web/VolumeTenantEndpoints.cs) | 租户与 Key 管理 |
| [`backend/Host/Web/VolumeAgentEndpoints.cs`](../backend/Host/Web/VolumeAgentEndpoints.cs) | 租户注册/查询/降级 |
| [`backend/Host/Security/`](../backend/Host/Security/) | JWT、角色解析、全局 RBAC、登录域注入 |
| [`backend/Infrastructure/Volumes/`](../backend/Infrastructure/Volumes/) | 分类器、对账、扫描、注册、查询、导入 |
| [`frontend/src/pages/Volume*.tsx`](../frontend/src/pages/) | 五个卷治理页面 |
| [`deploy/volume-manager/rbac.yaml`](../deploy/volume-manager/rbac.yaml) | ServiceAccount + ClusterRole + Binding |
| [`deploy/volume-manager/protection-policy.yaml`](../deploy/volume-manager/protection-policy.yaml) | VAP + Binding（默认 `Warn + Audit`） |
| [`deploy/volume-manager/namespace-labels.example.yaml`](../deploy/volume-manager/namespace-labels.example.yaml) | 受管命名空间打标示例 |
| [`deploy/volume-manager/kind-protection-test.sh`](../deploy/volume-manager/kind-protection-test.sh) | kind 删除保护端到端测试 |

### C. 上线顺序速查

| 阶段 | 动作 | 验收 |
| --- | --- | --- |
| P0 | Keycloak client/角色；PostgreSQL；`Auth:*` 与连接串 | 管理员能登录；匿名管理 API 401 |
| P0 | 部署 RBAC；命名空间打标；`Enabled=true` | 列表/报告可读；未标注卷完整 |
| P1 | 创建租户与 Key；业务注册；导入存量 | 覆盖率上升；冲突可解释 |
| P2 | 部署 VAP + Binding，保持 `Warn + Audit` | 审计日志中的「本会拦截」全部符合预期 |
| P3 | Binding 改为 `Deny` | 业务卷误删被拒；降级后可删除 |

P2 观察期不可跳过。详细命令、备份与故障处理见[运维手册](volume-manager-operations.md)。
