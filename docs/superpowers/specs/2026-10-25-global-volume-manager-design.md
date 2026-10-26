# 全局卷管理器（VolumeManager）设计规范

| 项 | 值 |
| --- | --- |
| 日期 | 2026-10-25 |
| 状态 | 待评审 |
| 目标框架 | .NET 10 / React 19 |
| 落地位置 | 本仓库：`backend/` + `frontend/` |
| 存储 | PostgreSQL |
| 问题证据 | `E:\GitHub\VolumeManager\Docs\卷管理现状分析-goodcrew与OpenSandbox.md` |
| 设计参考 | `E:\GitHub\VolumeManager\Docs\OpenSandbox-卷保护与盘点方案-v4.md`（本 spec 取代其实现范围） |

---

## 1. 背景与问题陈述

### 1.1 现状

1. **OpenSandbox 代建 PVC 时不留下可发现的标记。**
   唯一的 PVC 创建点在 OpenSandbox server 的 `kubernetes_service.py:844-866`；`deleteOnSandboxTermination=false`（默认）时 PVC 零标签、零注解。请求中的 `createIfNotExists` 不会写到 PVC 上。

2. **业务侧已有完整的卷生命周期体系，但只在单业务内可见。**
   goodcrew-pro 用确定性命名、`SandboxVolumeBindings` 台账、`VolumeReclaimPolicy` 决定归属与回收；沙箱删除从不连带删除 PVC。这套体系只覆盖 goodcrew 自己，且平台侧看不到。

3. **集群是多业务、多命名空间的。**
   生产、开发、后续新业务各自独立命名空间。平台需要一个跨业务、跨命名空间的统一治理入口。

4. **业务卷的存续没有独立于业务代码的删除保护。**
   一次错误的 `kubectl delete`、一次错误 selector 的批量删除，就能把业务持久数据删掉。

5. **发生过业务卷误删。**

### 1.2 结论

- 生命周期归属**只能由业务显式声明**，平台不从卷名、ownerReference、挂载状态推断。
- 删除保护必须在 apiserver 内执行，不能依赖本组件的可用性。
- 平台组件**永不删除卷**；回收执行保留在各业务自己的体系里。
- 归属声明需要一个全局台账，且台账必须能被业务以最小成本接入。

### 1.3 与既有设计的关系

本 spec **取代** `E:\GitHub\VolumeManager\Docs\OpenSandbox-卷保护与盘点方案-v4.md` 的实现范围：

- v4 的 GoodCrew.VolumeManager（独立只读组件 + `goodcrew/*` 标签 + 回填 CLI）**不再单独实现**。
- v4 的核心原则被完整继承：显式声明、非法值 fail-closed、未标注 fail-open、权威源不可读就停手、组件无删除能力。
- v4 的标签契约泛化为租户中立；v4 中 `goodcrew/owner = goodcrew:employee:...` 的示例**不符合 Kubernetes 标签值语法**（不允许冒号），本 spec 修正为 `<ownerKind>-<ownerId>`（见 §6.2）。
- VolumeManager 仓库的文档保留为设计参考，不再是实现依据。

---

## 2. 目标与非目标

### 2.1 目标

| 编号 | 目标 |
| --- | --- |
| G1 | 业务通过一个 API 声明卷归属；平台维护全局台账（租户、所有者、生命周期、状态） |
| G2 | 组件代写 PVC 标签，台账与标签天然一致；标签漂移可被发现 |
| G3 | 标记为业务持久卷的 PVC，在缺少授权身份时无法被删除（含 `kubectl delete`、错误 selector 批量删除） |
| G4 | 跨命名空间只读盘点：卷、归属、挂载状态、容量、生命周期、违规 |
| G5 | 卷消失可被事后发现与归因（合法回收 / 意外消失 / 命名空间级联） |
| G6 | 管理控制台提供完整运营界面：列表、详情、租户与 Key 管理、违规报告、回填导入 |
| G7 | 控制台由 Keycloak OIDC 保护，按分面角色（sandbox / volume × admin / viewer）授权；管理 API 默认拒绝匿名 |

### 2.2 非目标

| 编号 | 非目标 |
| --- | --- |
| NG1 | 不删除 PVC/PV，不提供任何删除卷的 API |
| NG2 | 不做 TTL、归档、reaper；不替代各业务的回收体系 |
| NG3 | 不从卷名、ownerReference、沙箱 metadata 推断归属 |
| NG4 | 不修改业务数据或业务台账 |
| NG5 | 不覆盖未打受管标记的命名空间 |
| NG6 | 不消费 Kubernetes audit log；归因只基于台账事件与观测历史 |
| NG7 | 通过本控制台沙箱界面直接创建的卷不自动注册（无租户上下文） |
| NG8 | 不做控制台内的租户级数据隔离：控制台角色是平台级；租户自助仅通过租户 API Key 的租户 API |

---

## 3. 术语

| 术语 | 定义 |
| --- | --- |
| **租户** | 使用 OpenSandbox 平台的一个业务（如 `goodcrew`）。一个租户拥有一个或多个命名空间 |
| **业务卷** | 承载业务持久数据的 PVC，删除需授权。标签 `opensandbox.io/volume-lifecycle=business` |
| **临时卷** | 业务声明可自行回收的 PVC。标签 `opensandbox.io/volume-lifecycle=ephemeral` |
| **未标注卷** | 无 lifecycle 标签的 PVC。不受保护，只进报告 |
| **台账** | `volumes` 表，归属的权威来源 |
| **观测** | `cluster_volumes` 表，集群当前状态的快照，由周期扫描写入 |
| **受管命名空间** | 带 `opensandbox.io/volume-managed=true` 标签的命名空间 |
| **声明** | 业务通过注册 API 明确写出卷的归属，而不是由平台推断 |
| **分面角色** | Keycloak 客户端角色 `sandbox:admin\|viewer`、`volume:admin\|viewer`；admin 含同面 viewer，两面互不蕴含 |

---

## 4. 需求

### 4.1 功能需求

#### FR-1 租户与 API Key

- 租户由平台管理员创建，包含：slug（如 `goodcrew`）、显示名、状态、允许的命名空间列表。
- slug 必须匹配 `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`，长度 ≤ 63。
- 每个租户可签发多个 API Key；Key 为 32 字节随机数的 base64url 编码（43 字符）。
- 库中只存 SHA-256 哈希与 8 字符前缀；明文只在签发响应中返回一次。
- Key 可吊销，吊销后下一次请求即失效。
- 租户只能注册/变更分配给自己的命名空间中的卷。

**验收**：Key 创建、鉴权、吊销、跨租户访问拒绝均有单测/接口测试。

#### FR-2 标签契约

PVC 使用以下标签（键与取值由单元测试逐字锁定）：

| 标签 | 必需 | 取值 | 作用 |
| --- | --- | --- | --- |
| `opensandbox.io/volume-lifecycle` | 参与保护判定 | `business` \| `ephemeral` | 唯一参与 CEL 判定的标签 |
| `opensandbox.io/volume-tenant` | 注册时必写 | 租户 slug | 归属与对账 |
| `opensandbox.io/volume-owner` | 建议 | `<ownerKind>-<ownerId>`，≤63 字符 | 归因；超长或含非法字符时省略 |
| `opensandbox.io/volume-contract-version` | 建议 | `1` | 契约演进 |

命名空间使用 `opensandbox.io/volume-managed=true` 标记受管范围。

分类语义（必须与 CEL 逐条一致）：

| `volume-lifecycle` 实际值 | 分类 | 受保护 |
| --- | --- | --- |
| 缺失 / 空白 | `Unlabeled` | 否（fail-open，只报告） |
| `ephemeral` | `Ephemeral` | 否 |
| `business` | `Business` | 是 |
| 其他任何值（含拼错、大小写不符） | `Business` | 是（fail-closed） |

**验收**：分类器单测覆盖上表四行；kind 端到端验证非法值同样被拒删。

#### FR-3 卷注册 API

- 批量注册（一次请求可含 workspace + memory 一对卷），允许部分成功，逐条返回结果。
- 幂等：同租户、同 owner 重复注册不产生第二行台账，只更新可变字段。
- 归属不可变：已登记的卷，`ownerKind` / `ownerId` 不允许被任何一方改写。
- 字段长度：`ownerKind` ≤ 32、`ownerId` ≤ 128、`role` ≤ 32、`mountPath` ≤ 128、`sandboxId` ≤ 128。
- 注册时同步写台账并 patch PVC 标签；标签 patch 失败时台账照常落库并标记 `LabelState=pending`，由扫描重试。
- PVC 不存在 → 该条 `not-found`；K8s 或数据库不可用 → 整个请求 503。
- 命名空间必须同时满足：已分配给该租户、带受管标签。否则该条 `invalid`。

**验收**：注册冲突矩阵（§6.5）逐行有测试；标签写入断言针对 fake `IKubernetes`。

#### FR-4 生命周期变更

- 租户可变更自己卷的 lifecycle（`business` ↔ `ephemeral`）。
- **降级为 `ephemeral` 必须提供 `reason`**，写入审计事件。
- 变更立即 patch PVC 标签；patch 失败时返回 `LabelState=pending` 并重试。
- lifecycle 变更只能通过本端点完成；注册 API 对已存在卷的 lifecycle 差异返回 `conflict`（避免绕过审计）。

**验收**：缺 reason 的降级被拒；降级后 CEL 放行删除；审计事件可查。

#### FR-5 盘点扫描

- 后台 Worker 按 `ScanInterval`（默认 5 分钟）扫描全部受管命名空间。
- 每轮：发现命名空间 → 每命名空间 list PVC + list Pod → 写入观测表。
- 已结束的 Pod（`Succeeded` / `Failed`）不算挂载。
- 单个命名空间 list 失败不影响其他命名空间；失败信息写入 `scan_runs`。
- 命名空间级并发受 `MaxConcurrentNamespaces` 限制。
- 支持管理端手动触发扫描。

**验收**：fake `IKubernetes` 覆盖多命名空间、挂载集合、单命名空间失败、命名空间 404 四类场景。

#### FR-6 对账与消失归因

对账必须遵循 §6.7 的规则，包括：

- **只有命名空间本轮 list 成功且清单中确实没有该 PVC 时，才允许标记 `missing`**；list 失败时不做任何状态迁移。
- 消失按台账 lifecycle 归因：`ephemeral` → 正常；`business` → `unexpected-disappearance` 高优先级违规。
- 注册后从未被观测到就消失 → `missing-before-observed` 高优先级违规。
- 批量消失检测：单轮同一命名空间消失数 ≥ `max(3, ceil(已知卷数 × 50%))` → `namespace-mass-disappearance`。
- 命名空间 404 → 其全部台账行标记 `missing`（原因 `namespace-deleted`）+ 一条批量事件。
- 命名空间存在但失去受管标签 → 台账行标记 `unmanaged`，**不判消失**。
- 同名 PVC 以新 UID 出现 → `reappeared-with-new-uid`，台账不自动迁移归属；租户重新注册后更新 `RegisteredUid` 并回到 `active`。
- 观测到 `deletionTimestamp` → 记录 terminating，不立即判消失。

**验收**：对账判定为纯函数，逐条覆盖 §6.7 表格；扫描器集成测试验证状态迁移。

#### FR-7 删除保护

- 在受管命名空间中，对 lifecycle 标签存在且不等于 `ephemeral` 的 PVC 发起 `DELETE` 时，apiserver 必须拒绝，除非请求者属于 `opensandbox-volume-approvers` 组。
- `system:masters` 不自动豁免。
- 保护由 `ValidatingAdmissionPolicy` 执行，组件宕机不影响保护效果。
- 上线节奏：先 `Warn + Audit`，观察后加入 `Deny`。

**验收**：kind 集群中，审批组内/外两个身份分别删除同一业务卷，一个成功一个被拒；停止组件后保护仍生效。

#### FR-8 管理查询

- `GET /api/v1/volumes` 支持服务端分页、筛选、搜索、排序。
- 语义（吸取 goodcrew 的线上教训）：筛选 → 搜索 → 排序 → **最后切片**；`total` 为筛选后全量；排序带卷名决胜键；页码越界返回空页；依赖不可用返回 503，不返回空清单。
- 合并视图 = 台账 LEFT JOIN 观测，派生出 §6.9 的分类值。

**验收**：分页语义逐条测试；越界页、搜索跨页、排序稳定性各有用例。

#### FR-9 回填导入

- 管理员通过 API/UI 导入存量卷映射（命名空间 / claimName / 租户 / owner / lifecycle）。
- **默认 `dry-run`**，显式 `dryRun=false` 才写入。
- 逐条结果；映射中没有的卷不打标签、不改台账。
- 目标不存在、归属冲突、值非法 → 逐条报告，不中止整批。
- 平台不根据卷名或引用关系自动生成映射。

**验收**：dry-run 不产生写操作；非法值逐条拒绝；已冲突卷不被覆盖。

#### FR-10 报告

`GET /api/v1/volumes/report` 提供：

- 最近一次扫描的时间、耗时、per-namespace 错误
- 总量与分类计数：observed / registered / unlabeled / unregistered / drift / missing / reappeared / unmanaged
- 标签覆盖率与注册覆盖率
- 按租户、按命名空间的分布
- 违规清单：未标注、未注册、漂移、意外消失、失去受管命名空间、同名新 UID 重现、未知租户标签
- 列表超过 200 条时截断并给出 `truncated=true`，完整清单走筛选后的卷列表

**验收**：固定输入快照测试。

#### FR-11 审计事件

所有状态变更写 `volume_events`（append-only，无修改/删除 API）：

| 事件 | 触发 |
| --- | --- |
| `registered` / `updated` / `reactivated` | 注册成功的三类结果 |
| `registration-conflict` | 注册被拒 |
| `label-applied` / `label-patch-failed` / `label-corrected` | 标签写入结果 |
| `lifecycle-changed` | 生命周期变更（含 reason） |
| `imported` | 导入写入 |
| `drift-detected` / `drift-resolved` | 违规集合发生变化时各记一次 |
| `disappeared` / `unexpected-disappearance` | 消失归因 |
| `missing-before-observed` | 注册后从未被观测就消失 |
| `namespace-mass-disappearance` | 批量消失 |
| `namespace-unmanaged` | 命名空间失去受管标签 |
| `namespace-managed-again` | 命名空间重新受管 |
| `reappeared-with-new-uid` | 同名重建 |
| `terminating-observed` | 观测到删除中 |

**验收**：每个状态迁移都有对应事件测试。

#### FR-12 管理界面

| 路由 | 页面 | 内容 |
| --- | --- | --- |
| `/volumes` | 卷列表 | 服务端分页表格；筛选（命名空间/租户/分类/挂载）；搜索；最后扫描时间、覆盖率、立即扫描；行操作：降级/升级（弹窗 + 必填原因） |
| `/volumes/:namespace/:claimName` | 卷详情 | 台账卡片、集群观测卡片（phase / storageClass / 容量 / 挂载 Pod / 原始标签）、事件时间线、违规徽标 |
| `/volume-tenants` | 租户管理 | 租户列表与统计；新建租户；签发 Key（明文一次性展示）与吊销 |
| `/volume-report` | 违规报告 | 分类统计卡 + 违规分表 + 扫描错误 |
| `/volume-import` | 回填导入 | CSV/粘贴 → 预览 → dry-run 结果 → 确认写入 |

侧边栏新增「卷管理」组。前端沿用 React Router + Ant Design + TanStack Query + Vitest。

#### FR-13 控制台认证与角色

- 控制台全部页面与全部管理 API（`/api/v1/sandboxes*`、`/api/v1/volumes*`、`/api/v1/volume-tenants*`、`/api/v1/console/*`）要求 Keycloak 登录；匿名只允许健康检查、静态资源与 SPA 入口。
- 后端校验 JWT 的 issuer、有效期与 `azp == ClientId`；不校验 `aud`（Keycloak 的 `aud` 恒为 `account`）。角色只读 `resource_access.<ClientId>.roles`。
- 角色为 `sandbox:admin|viewer`、`volume:admin|viewer`；admin 含同面 viewer，两面互不蕴含。
- 默认拒绝：未登记规则的 `/api/*` 一律拒绝；角色不足返回 403；未认证返回 401。
- 租户 API（`/api/v1/tenant/volumes*`）继续使用每租户 API Key；控制台 JWT 不能调用租户 API，租户 Key 不能调用管理 API。
- 不再存在 `AdminApiKey` 管理后门。
- 前端用授权码 + PKCE(S256) 登录，token 存 `sessionStorage`，到期前静默续期，401 时清空并重登；登录域由后端运行时注入首页。
- 无任何角色的账号可以完成登录，但只能看到「无权限」页并退出。

**验收**：§6.4 权限矩阵逐格测试；`azp` 不符、issuer 不符、过期、无 token 全部 401；租户 Key 与控制台 JWT 互不越界；前端登录、续期、退出与无权限页有 Vitest 用例。

### 4.2 非功能需求

| 编号 | 需求 | 验收 |
| --- | --- | --- |
| NFR-1 | 保护不依赖组件可用性：组件宕机时 CEL 照常拦截 | 停组件后重复 FR-7 测试 |
| NFR-2 | 组件无删除能力：RBAC 无 `delete` | 检查 ClusterRole 清单 |
| NFR-3 | 20 个受管命名空间、5000 个 PVC 时，单轮扫描 ≤ 4 分钟；每轮每命名空间只 list PVC 与 Pod 各一次 | 性能测试 |
| NFR-4 | 分页语义正确：不退化、不空转、不越界回第一页 | FR-8 用例 |
| NFR-5 | 依赖降级：K8s 不可用 → 扫描暂停、注册 503；数据库不可用 → 管理读取 503；均不得返回「空清单」 | 故障注入测试 |
| NFR-6 | 密钥安全：库中只存哈希；日志不得出现明文 Key | 代码审查 + 日志断言 |
| NFR-7 | 审计 append-only：无更新/删除事件的 API | 端点清单审查 |
| NFR-8 | 标签值合法：owner 标签按 §6.2 规范化，超限时省略而非截断成错误值 | 单测 |
| NFR-9 | 控制台 API 默认拒绝：匿名 401；已登录但角色不足 403；未登记规则的 `/api/*` 同样拒绝 | RBAC 矩阵测试 + 端点清单审查 |
| NFR-10 | 登录域只由后端运行时注入；生产前端构建不内置 realm 回落；`Auth` 配置缺失时后端启动失败 | 配置注入测试 + 打包产物审查 |

### 4.3 约束

| 编号 | 约束 |
| --- | --- |
| C-1 | 集群需 Kubernetes ≥ 1.30（`ValidatingAdmissionPolicy` GA）。1.28–1.29 需特性门控；低于 1.28 必须回退 Webhook |
| C-2 | CEL 是静态规则，不能查询台账或外部系统 |
| C-3 | OpenSandbox 的卷请求体没有 metadata 字段，标签只能在 PVC 创建后写入 |
| C-4 | 删除命名空间会级联删除 PVC，不经过 PVC 的 admission |
| C-5 | 控制台依赖 Keycloak 登录；Keycloak 不可用时控制台不可登录（删除保护、扫描与租户 API 不受影响） |
| C-6 | 组件需要 PVC `patch` 权限；RBAC 无法限制到标签键级 |
| C-7 | Keycloak 侧需为控制台创建 public client 与四个客户端角色（部署前置，见 §6.12） |

---

## 5. 架构

### 5.1 总体架构

```text
浏览器（管理员）
   │ ① 授权码 + PKCE → Keycloak
   │ ② Bearer JWT → 管理 API
   ▼
Keycloak（realm 内 public client：opensandbox-web）
   └── discovery / JWKS ──► OpenSandBoxWeb（JwtBearer 校验）

Kubernetes API Server
├── ValidatingAdmissionPolicy (CEL)  ← 拦截 PVC DELETE（独立于组件可用性）
├── Namespace labels: opensandbox.io/volume-managed=true
└── PVC labels: volume-lifecycle / volume-tenant / volume-owner / volume-contract-version
        ▲                          ▲
        │ list（扫描）              │ patch（注册/降级时）
        │                          │
┌───────┴──────────────────────────┴─────────────────────────┐
│ OpenSandBoxWeb · VolumeManager 模块                         │
│  ┌─────────────────┐  ┌──────────────────┐  ┌───────────┐  │
│  │ 租户注册 API     │  │ 盘点 Worker       │  │ 管理 API  │  │
│  │ 每租户 API Key   │  │ PVC + Pod 扫描    │  │ 列表/详情 │  │
│  │ 台账 + 标签 patch│  │ 对账 → 违规报告   │  │ 导入/扫描 │  │
│  └────────┬────────┘  └────────┬─────────┘  └─────┬─────┘  │
│           └────────────┬───────┴──────────────────┘         │
│                  PostgreSQL（台账 + 观测 + 审计）            │
└─────────────────────────────────────────────────────────────┘
        ▲
        │ 注册 / 降级（每租户 API Key）
   业务后端（goodcrew-pro、后续业务）
```

保护由 apiserver 执行，组件不在关键路径上。

### 5.2 职责边界

| 事项 | 归属 |
| --- | --- |
| 卷的创建（`createIfNotExists`） | OpenSandbox server |
| 归属声明（注册 / lifecycle 变更） | 业务 → 全局 VolumeManager API |
| 台账与审计 | 全局 VolumeManager（PostgreSQL） |
| PVC 标签写入 | 全局 VolumeManager |
| 删除保护 | 集群 CEL 策略 |
| 盘点与对账 | 全局 VolumeManager 后台 Worker |
| 回收决策与执行 | **各业务**（goodcrew 的 `VolumeReclaimPolicy` / `VolumeInventoryService` 不变） |
| 控制台登录与角色 | Keycloak OIDC（JwtBearer 校验 + 分面角色） |

### 5.3 后端代码结构

```text
backend/
├── Contracts/
│   └── VolumeContracts.cs                 # 请求/响应 DTO
├── Host/Web/
│   ├── VolumeAgentEndpoints.cs            # /api/v1/tenant/volumes（租户 API）
│   ├── VolumeAdminEndpoints.cs            # /api/v1/volumes、/api/v1/volume-tenants
│   └── ConsoleEndpoints.cs                # /api/v1/console/me
├── Host/Security/
│   ├── ConsoleAuth.cs                     # JwtBearer + FallbackPolicy + 401/403 响应
│   ├── ConsoleRoles.cs                    # resource_access.<clientId>.roles 解析
│   ├── ConsoleRbac.cs                     # 全局 RBAC 中间件（路径 + 方法 → 角色）
│   ├── TenantApiKeyMiddleware.cs          # 租户 Key → 已认证主体（供 FallbackPolicy）
│   └── AuthConfigInjection.cs             # 登录域注入 index.html
├── Infrastructure/
│   ├── DependencyInjection/
│   │   └── VolumeManagerRegistration.cs   # DI 注册 + 配置绑定
│   └── Volumes/
│       ├── Persistence/                   # DbContext、实体、迁移
│       ├── VolumeClassifier.cs            # 纯函数：标签 → 分类
│       ├── VolumeReconciliation.cs        # 纯函数：观测 + 台账 → 状态迁移
│       ├── RegistrationService.cs         # 注册/生命周期变更
│       ├── VolumeLabelWriter.cs           # PVC 标签 merge patch
│       ├── InventoryScanner.cs            # BackgroundService
│       ├── VolumeQueries.cs               # 列表/详情/报告查询
│       ├── ApiKeyAuthenticator.cs         # 每租户 Key 鉴权
│       └── KubernetesVolumeClient.cs      # IKubernetes 包装（只读 + patch）
├── Program.cs                             # 组装入口（新增两行注册与映射）
└── Migrations/                            # EF Core 迁移（或 Persistence/Migrations）
```

---

## 6. 详细设计

### 6.1 标签契约

| 位置 | 键 | 值 |
| --- | --- | --- |
| Namespace | `opensandbox.io/volume-managed` | `"true"` |
| PVC | `opensandbox.io/volume-lifecycle` | `business` \| `ephemeral` |
| PVC | `opensandbox.io/volume-tenant` | 租户 slug |
| PVC | `opensandbox.io/volume-owner` | `<ownerKind>-<ownerId>`（规范化后 ≤63 字符） |
| PVC | `opensandbox.io/volume-contract-version` | `1` |

**owner 标签规范化**（修正 v4 的冒号写法）：

1. 取 `ownerKind + "-" + ownerId`。
2. 将 `[^A-Za-z0-9_.-]` 的字符替换为 `-`。
3. 若结果不以字母数字开头/结尾，或长度 > 63 → **省略标签**（台账仍有精确值）。
4. 对账时只比较「应写且已写」的标签；省略的 owner 不产生违规。

**标签写入是 merge patch**，只触碰上述键，不删除其他标签。降级只改 lifecycle，保留 tenant/owner 标签。

### 6.2 数据模型

#### `tenants`

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | text PK | slug，如 `goodcrew` |
| `DisplayName` | text | 显示名 |
| `Status` | text | `active` \| `disabled` |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

#### `tenant_namespaces`

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Namespace` | text PK | **全局唯一**，一个命名空间只能属于一个租户 |
| `TenantId` | text FK | |

#### `tenant_api_keys`

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | uuid PK | |
| `TenantId` | text FK | |
| `Name` | text | 用途备注 |
| `KeyHash` | bytea | SHA-256（Key 为高熵随机值，无需慢哈希） |
| `Prefix` | text | 前 8 字符，用于识别 |
| `CreatedAt` / `RevokedAt` / `LastUsedAt` | timestamptz | `LastUsedAt` 每 5 分钟最多更新一次 |

#### `volumes`（台账）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | uuid PK | v7 |
| `Namespace` + `ClaimName` | text | **唯一索引** |
| `TenantId` | text FK | |
| `OwnerKind` / `OwnerId` / `Role` | text | 泛化自 goodcrew `SandboxVolumeBinding` |
| `MountPath` | text null | `/workspace`、`/app/memory` 等 |
| `Lifecycle` | text | `business` \| `ephemeral`（API 权威的期望状态） |
| `ContractVersion` | text | 默认 `1` |
| `LastSandboxId` | text null | 取证线索，不清空 |
| `RegisteredUid` | text null | 注册/重新注册时观测到的 PVC UID |
| `State` | text | `active` \| `missing` \| `unmanaged` |
| `StateReason` | text null | `ephemeral-gone` \| `unexpected` \| `namespace-deleted` \| `never-observed` |
| `MissingSince` | timestamptz null | |
| `LabelState` | text | `applied` \| `pending` |
| `Violations` | jsonb | 当前违规集合；变化时产生事件（去重用） |
| `RegisteredAt` / `UpdatedAt` / `LastSeenAt` | timestamptz | |

索引：`(Namespace, ClaimName)` 唯一；`TenantId`；`State`；`Lifecycle`。

#### `cluster_volumes`（观测，只保留当前存在的卷）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Namespace` + `ClaimName` | text PK | |
| `Uid` | text | 同名重建检测 |
| `Phase` | text | `Bound` / `Pending` / `Lost` |
| `StorageClass` | text null | |
| `RequestedStorage` / `Capacity` | text null | |
| `CreatedAtK8s` | timestamptz | |
| `DeletionTimestamp` | timestamptz null | terminating 检测 |
| `Mounted` | bool | 是否被未结束的 Pod 挂载 |
| `MountedBy` | jsonb | Pod 名列表 |
| `Labels` | jsonb | 原始标签快照 |
| `LifecycleLabel` / `TenantLabel` / `OwnerLabel` / `ContractVersionLabel` | text null | 契约标签的显式列（索引/比较用） |
| `FirstSeenAt` / `LastSeenAt` | timestamptz | |
| `LastScanId` | uuid FK | |

#### `managed_namespaces`（命名空间发现状态）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Namespace` | text PK | |
| `FirstSeenAt` / `LastSeenAt` | timestamptz | |
| `State` | text | `active` \| `gone` \| `unmanaged` |

#### `scan_runs`

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | uuid PK | |
| `Trigger` | text | `scheduled` \| `manual` \| `startup` |
| `StartedAt` / `FinishedAt` | timestamptz | |
| `NamespacesScanned` / `NamespacesSucceeded` / `VolumeCount` | int | |
| `Errors` | jsonb | `[{ namespace, message }]` |

#### `volume_events`（append-only 审计）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | uuid PK | v7 |
| `VolumeId` | uuid null | 无台账行的观测卷事件可为 null |
| `Namespace` + `ClaimName` | text | |
| `TenantId` | text null | |
| `Type` | text | §4.1 FR-11 的事件类型 |
| `Actor` | text | `tenant:<slug>` \| `admin` \| `scanner` \| `system` |
| `Reason` | text null | 降级原因等 |
| `Data` | jsonb | 事件附加信息 |
| `CreatedAt` | timestamptz | |

索引：`(Namespace, ClaimName, CreatedAt desc)`、`VolumeId`。台账行**永不物理删除**。

### 6.3 分类派生（列表与报告的统一口径）

`classification` 按以下优先级计算：

| 条件 | classification |
| --- | --- |
| 台账 `State=unmanaged` | `unmanaged` |
| 台账 `State=missing` 且无观测 | `missing` |
| 台账 `State=missing` 且有观测且 `Uid ≠ RegisteredUid` | `reappeared` |
| 台账存在 + 观测存在 + `Violations` 非空 | `drift` |
| 台账存在 + 观测存在 + 无违规 | `registered` |
| 无台账 + 有 lifecycle 标签 | `unregistered` |
| 无台账 + 无 lifecycle 标签 | `unlabeled` |

`unlabeled` 与 `unregistered` 为只读派生值，不写台账；它们进报告，驱动回填。

### 6.4 鉴权

三个面、三套凭据、互不通用。

| 面 | 凭据 | 校验 |
| --- | --- | --- |
| 控制台 UI + 管理 API | Keycloak access token（`Authorization: Bearer <JWT>`） | issuer、有效期、`azp == ClientId`；角色见下 |
| 租户 API（`/api/v1/tenant/volumes*`） | 每租户 API Key（`Authorization: Bearer <key>`） | SHA-256 哈希查表；未吊销；租户 `active`；命名空间归属 |
| 健康检查 / 静态资源 | 匿名 | — |

**JwtBearer 参数**（仿 goodcrew-pro `AuthExtensions`，2026-08-03 Spec §8）：

- `Authority = Auth:Authority`（配置值统一去除尾部 `/`），`MapInboundClaims = false`，`RequireHttpsMetadata = Auth:RequireHttpsMetadata`（默认 `true`）。
- `ValidateIssuer = true`，`ValidIssuer = Authority`（去除尾部 `/` 后精确匹配）；`ValidateLifetime = true`，`ClockSkew = 30s`。
- `ValidateAudience = false`：Keycloak access token 的 `aud` 恒为 `account`，按 `aud` 校验会拒掉所有合法令牌。改在 `OnTokenValidated` 校验 `azp == Auth:ClientId`，缺失 `azp` 同样拒绝。
- `NameClaimType = preferred_username`；`/console/me` 的 `subject` 取 `sub`，`username` 取 `preferred_username`（缺失时回落 `sub` 前 8 位）。
- `OnChallenge` → 401 `{code:"unauthorized", message}`；`OnForbidden` → 403 `{code:"forbidden", message, roles}`。
- `Authorization: FallbackPolicy = RequireAuthenticatedUser()`；`Auth:Authority` / `Auth:ClientId` 缺失时启动即抛异常。

**角色解析**：

- 只读 `resource_access.<ClientId>.roles`；不读 `realm_access`，不读其他客户端的 `resource_access`。
- claim 缺失、JSON 畸形、`roles` 非数组 → 视为无角色（fail-closed）。
- 角色：`sandbox:admin`、`sandbox:viewer`、`volume:admin`、`volume:viewer`；同一面内 `admin` 满足 `viewer` 的要求。

**权限矩阵**（`UseConsoleRbac`，按路径前缀 + 方法判定）：

| 路径 | 匿名 | sandbox:viewer | sandbox:admin | volume:viewer | volume:admin |
| --- | --- | --- | --- | --- | --- |
| `/api/v1/sandboxes*` GET（含 metrics / SSE） | 401 | ✓ | ✓ | 403 | 403 |
| `/api/v1/sandboxes*` 非 GET（create / pause / resume / renew / delete） | 401 | 403 | ✓ | 403 | 403 |
| `GET /api/v1/sandboxes/{id}/endpoints/{port}` | 401 | 403 | ✓ | 403 | 403 |
| `/api/v1/volumes*`、`/api/v1/volume-tenants*` GET | 401 | 403 | 403 | ✓ | ✓ |
| 上两组的非 GET（scan / import / lifecycle / 租户与 Key 管理） | 401 | 403 | 403 | 403 | ✓ |
| `GET /api/v1/console/me` | 401 | ✓ | ✓ | ✓ | ✓ |
| `/api/v1/tenant/*` | 租户 Key | — | — | — | — |
| 其他 `/api/*` | 401 | 403 | 403 | 403 | 403 |
| `/api/v1/health`、`/healthz`、`/readyz` | ✓ | ✓ | ✓ | ✓ | ✓ |
| 静态资源、SPA fallback | ✓ | ✓ | ✓ | ✓ | ✓ |

- GET/HEAD 视为读；OPTIONS 放行（生产同源、开发经 Vite proxy，不产生预检）。
- 规则按路径前缀匹配；`GET .../endpoints/{port}` 单列为 admin 例外，先于通用沙箱 GET 规则。
- `GET .../endpoints/{port}` 返回可直接访问沙箱数据面的 URL，按 admin 处理，不当作普通只读。
- **未匹配的 `/api/*` 拒绝**：新增端点必须先在本表登记，漏登记只会更严，不会漏成洞。
- 健康检查端点与 SPA fallback 显式 `AllowAnonymous()`，否则 FallbackPolicy 会拦住匿名访问。
- 无任何角色的账号可以登录（`/console/me` 返回空 roles），其余管理 API 一律 403；前端显示无权限页。
- 租户 Key 中间件位于 `UseAuthentication` 之前：命中 `/api/v1/tenant` 时校验 Key 并把租户主体写入 `HttpContext.User`，其余路径直接放行给 JwtBearer。FallbackPolicy 仍然成立；租户主体不带 `resource_access`，无法通过管理 API 的角色检查。

### 6.5 注册与冲突矩阵

```http
POST /api/v1/tenant/volumes/register
Authorization: Bearer <tenant-key>
Content-Type: application/json

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

响应 200（允许部分成功）：

```json
{
  "results": [
    { "claimName": "trainer-ws-...-meta", "status": "registered", "volumeId": "..." }
  ]
}
```

`status` 取值：`registered` \| `updated` \| `reactivated` \| `conflict` \| `not-found` \| `invalid` \| `label-pending`。

**冲突矩阵**：

| 台账现状 | 结果 |
| --- | --- |
| 无台账行；PVC 存在；标签无冲突 | 建台账 + patch 标签 → `registered` |
| 同租户同 `ownerKind`/`OwnerId`；lifecycle 与请求一致 | 更新 `Role`/`MountPath`/`LastSandboxId`、重试标签 → `updated` |
| 同租户同 owner；lifecycle 与请求不同 | **`conflict`**（要求走 lifecycle 端点，保留审计） |
| 同租户同 owner；台账 `missing`；PVC 已重建 | 更新 `RegisteredUid`、回 `active`、应用请求 lifecycle → `reactivated` |
| 不同租户或不同 owner | **`conflict`**，不写任何标签 |
| PVC 不存在 | `not-found`（调用方退避重试） |
| 命名空间未分配给租户 / 未打受管标签 | `invalid` |
| 标签 patch 失败（台账已落库） | `label-pending`，扫描时重试 |

注册校验顺序：Key → 租户状态 → 命名空间（分配 + 受管）→ 字段合法性 → PVC 存在 → 台账冲突 → 写台账 → patch 标签。

owner 唯一性按租户**跨命名空间**判定：同一租户的 `ownerKind`/`ownerId` 已绑定其他 `(namespace, claimName)` 时返回 `conflict`，不产生第二行台账。

`reactivated` 时若请求 lifecycle 与旧台账不同，追加 `lifecycle-changed` 事件（`reason=reactivation`）。

### 6.6 生命周期变更

```http
POST /api/v1/tenant/volumes/{namespace}/{claimName}/lifecycle
{ "lifecycle": "ephemeral", "reason": "trial round finished" }
```

- 只允许卷所属租户或管理员。
- `business → ephemeral` 必须带 `reason`，否则 400。
- 台账先更新，再 patch 标签；写 `lifecycle-changed` 事件（含 actor、reason）。
- 标签 patch 失败 → `LabelState=pending`，响应中返回该状态。

**合法删除路径**：租户调本端点降级 → 组件把标签改为 `ephemeral` → CEL 放行 → 租户自己删除 PVC → 下一轮扫描发现消失，按 `ephemeral-gone` 记为正常回收。

管理员直接删除受保护卷（加入审批组、先移除标签、删命名空间）都会在台账中留下「意外消失」或「失去受管」记录；最终归因对照 k8s audit log。

### 6.7 盘点与对账

#### 6.7.1 扫描算法

1. 创建 `scan_run`（`started`）。
2. 用 `labelSelector=opensandbox.io/volume-managed=true` 列出命名空间，更新 `managed_namespaces`。
3. 对上一轮存在、本轮不再出现的命名空间：
   - `GET namespace` 返回 404 → 该命名空间全部台账行 → `missing`（`namespace-deleted`）+ 一条 `namespace-mass-disappearance` 事件；
   - 命名空间存在 → 台账行 → `unmanaged` + `namespace-unmanaged` 事件。
4. 对每个受管命名空间（并发 ≤ `MaxConcurrentNamespaces`）：
   - list PVC（全量，含未标注）；list Pod。
   - **任一失败 → 记录错误并跳过该命名空间的全部状态迁移**（不删除观测、不标 missing）。
   - 成功 → upsert `cluster_volumes`；删除该命名空间中本轮不存在的观测行；更新 `LastSeenAt`。
5. 对账（§6.7.2）。
6. 标签重试：`LabelState=pending` 且本轮观测到 PVC 的台账行，重新 patch。
7. 批量消失检测（§6.7.3）。
8. 结束 `scan_run`，写入计数与错误。

#### 6.7.2 台账状态迁移

| 条件 | 结果 |
| --- | --- |
| 台账 `active`；命名空间扫描成功；PVC 存在 | 保持 `active`，更新 `LastSeenAt`，重算 `Violations` |
| 台账 `active`；命名空间扫描失败 | 不动 |
| 台账 `active`；命名空间扫描成功；PVC 不存在；`LastSeenAt` 为 null | → `missing`（`never-observed`），事件 `missing-before-observed`（高优先级） |
| 台账 `active`；命名空间扫描成功；PVC 不存在；`Lifecycle=ephemeral` | → `missing`（`ephemeral-gone`），事件 `disappeared`（信息级） |
| 台账 `active`；命名空间扫描成功；PVC 不存在；`Lifecycle=business` | → `missing`（`unexpected`），事件 `unexpected-disappearance`（高优先级） |
| 台账 `missing`；PVC 出现且 UID = `RegisteredUid` | → `active`（系统纠正，正常不会发生），事件 `reactivated`（actor=system） |
| 台账 `missing`；PVC 以新 UID 出现 | 保持 `missing`，事件 `reappeared-with-new-uid`；等租户重新注册 |
| 台账 `missing`；租户重新注册且 PVC 存在 | → `active`，更新 `RegisteredUid` |
| 台账 `unmanaged`；命名空间重新受管且 PVC 存在 | → `active`，事件 `namespace-managed-again` |

**判定顺序自上而下**：`never-observed` 优先于按 lifecycle 归因；`missing` 行的 PVC 出现时先比较 UID。

**判定依据是台账中的 lifecycle（API 权威），不是标签当前值。** 标签被外部改动属于漂移，单独记录。

#### 6.7.3 批量消失

单轮同一命名空间中 `missing` 迁移数 ≥ `max(3, ceil(已知台账行数 × 0.5))` → 追加一条 `namespace-mass-disappearance` 事件；逐条消失事件仍保留。

命名空间 404 的场景**无条件**产生批量事件，且**不逐条写** `unexpected-disappearance`（原因已明确为 `namespace-deleted`），避免一次命名空间删除刷出成百上千条高优先级告警。

#### 6.7.4 漂移与违规

台账行与观测标签比较（仅在台账 `State=active`、观测存在且 `LabelState=applied` 时；`pending` 表示标签尚未成功写入，不算漂移，只在详情页与事件中体现；`missing` + 新 UID 的场景只记 `reappeared-with-new-uid`，不参与漂移比较）：

| 违规 | 条件 | 级别 |
| --- | --- | --- |
| `labels-missing` | 台账 `active`，PVC 上无任何契约标签 | 高 |
| `lifecycle-mismatch:<value>` | 标签值 ≠ 台账 lifecycle | 高 |
| `tenant-mismatch:<value>` | 标签租户 ≠ 台账租户 | 高 |
| `owner-mismatch:<value>` | 应写 owner 标签但值不符 | 中 |
| `unsupported-contract-version:<v>` | 版本不为 `1` | 中 |
| `unknown-lifecycle:<v>` | 标签值非法（分类按 Business） | 高 |
| `uid-changed` | 观测 UID ≠ `RegisteredUid` | 中 |

`Violations` 集合变化时写 `drift-detected` / `drift-resolved` 事件；未变化不重复写。

无台账行的观测卷在读取时派生：

- 无 lifecycle 标签 → `unlabeled`
- 有 lifecycle 标签 → `unregistered`；若标签租户不是已注册租户 → 附 `unknown-tenant:<slug>`

### 6.8 删除保护（CEL）

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
  # P2 阶段只观察；P3 阶段加入 Deny
  validationActions: ["Warn", "Audit"]
  matchResources:
    namespaceSelector:
      matchLabels:
        opensandbox.io/volume-managed: "true"
```

要点：

- `validationActions` 属于 Binding，不属于 Policy。
- `system:masters` 不自动豁免。
- 审批组初始成员：平台运维负责人；业务租户**不入组**（业务通过降级端点自助删除）。
- 移除标签是逃生通道，进审计日志。

### 6.9 API 清单

管理 API 全部要求 `Authorization: Bearer <JWT>`，所需角色见 §6.4；租户 API 只认每租户 API Key。

#### 租户 API（每租户 Key）

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| POST | `/api/v1/tenant/volumes/register` | 批量注册/更新 |
| POST | `/api/v1/tenant/volumes/{namespace}/{claimName}/lifecycle` | 变更 lifecycle |
| GET | `/api/v1/tenant/volumes` | 查看自己的卷（分页同管理列表，强制租户过滤） |

#### 管理 API（控制台）

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| GET | `/api/v1/console/me` | 当前账号、subject 与角色（任何已登录用户） |
| GET | `/api/v1/volumes` | 分页/筛选/搜索/排序 |
| GET | `/api/v1/volumes/{namespace}/{claimName}` | 详情 + 事件时间线 |
| POST | `/api/v1/volumes/{namespace}/{claimName}/lifecycle` | 管理员变更 lifecycle |
| POST | `/api/v1/volumes/scan` | 立即扫描 |
| GET | `/api/v1/volumes/report` | 汇总、覆盖率、违规、扫描错误 |
| POST | `/api/v1/volumes/import` | 回填导入（默认 dry-run） |
| GET | `/api/v1/volume-tenants` | 租户列表（含统计） |
| POST | `/api/v1/volume-tenants` | 新建租户 |
| PATCH | `/api/v1/volume-tenants/{id}` | 修改显示名/状态/命名空间 |
| GET | `/api/v1/volume-tenants/{id}/keys` | Key 列表（不含明文） |
| POST | `/api/v1/volume-tenants/{id}/keys` | 签发 Key（明文一次性返回） |
| POST | `/api/v1/volume-tenants/{id}/keys/{keyId}/revoke` | 吊销 |

**列表查询参数**：`page`（默认 1）、`pageSize`（默认 20，上限 200）、`namespace`、`tenant`、`classification`（§6.3 七值）、`lifecycle`、`mounted`、`q`（claimName/owner/tenant 子串，大小写不敏感）、`sort`（`namespace` \| `claimName` \| `createdAt` \| `lastSeenAt` \| `tenant`）、`order`（`asc` \| `desc`）。

**导入请求**：

```http
POST /api/v1/volumes/import
{
  "dryRun": true,
  "items": [
    {
      "namespace": "opensandbox",
      "claimName": "trainer-ws-3f2a9c1b7d4e-meta",
      "tenant": "goodcrew",
      "ownerKind": "employee",
      "ownerId": "3f2a9c1b7d4e",
      "role": "meta",
      "lifecycle": "business"
    }
  ]
}
```

响应 200：`{ "results": [{ "namespace": "...", "claimName": "...", "status": "would-register", "message": "..." }] }`。`dryRun` 默认 `true`；逐条状态与注册 API 同构，另加 `would-register` / `would-update`。

**错误语义**：400 参数非法；401 未登录 / token 无效 / 租户 Key 无效；403 角色不足或命名空间不属于该租户；404 未找到；409 冲突；503 依赖不可用。批量注册返回 200 + 逐条状态。

`/api/v1/console/me` 响应：

```json
{ "subject": "…", "username": "zhangsan", "roles": ["volume:admin", "sandbox:viewer"] }
```

只返回四个已知角色；客户端上配置的其他角色不出现在响应中，也不影响授权。

无删除端点。

### 6.10 前端设计

- `src/auth/`：`authModel.ts`（纯函数：登录域解析、授权/登出 URL、回调解析、续期判定）、`pkce.ts`（S256，用 `@noble/hashes` 兼容 HTTP 内网）、`auth.ts`（sessionStorage 存取、静默续期、`login` / `logout` / `ensureAuthenticated`）、`AuthGate.tsx`（启动门控）、`AuthFailure.tsx`、`AuthContext.tsx`（`/console/me` 结果）。
- 启动顺序：`ensureAuthenticated()` 处理回调 / 复用未过期 token / 否则跳转 Keycloak，期间显示启动屏。回调失败显示失败页（重新登录 / 退出），不静默重跳。`returnTo` 随 state 交易存 `sessionStorage`，交易 15 分钟过期且只能换票一次，登录后恢复深链。
- token 存 `sessionStorage`（关标签页即登出）；到期前 60 秒续期，并发的续期请求共用一个 Promise；续期失败清空并重登。
- 退出走 Keycloak end-session（`id_token_hint` + `post_logout_redirect_uri`），同时清空本地 token。
- `apiClient` 增加请求拦截器附加 `Authorization: Bearer`；响应拦截器遇 401 清 token 并重登，403 只把错误交给页面。
- `useMetricsStream` 已使用 `fetch`，改为带 `Authorization` 头；401 触发重登；不把 token 放进 URL。
- 布局：Header 右侧显示 `preferred_username`、角色标签与退出；菜单与写操作按角色门控（§6.4 矩阵）；`/` 按角色落到 `/dashboard` 或 `/volumes`；无任何角色时显示无权限页。
- 设置页显示当前账号、subject、角色与登录域（authority / clientId），不提供任何密钥输入。
- 登录域：优先读后端注入的 `window.__OPENSANDBOX_AUTH__`；仅 `import.meta.env.DEV` 下回落到 `VITE_AUTH_AUTHORITY` / `VITE_AUTH_CLIENT_ID`；生产构建两者都没有时启动即报错。
- `src/api/volume.ts`：列表、详情、报告、扫描、导入、租户与 Key 管理、lifecycle 变更。
- `src/api/types.ts`：`VolumeListItem`、`VolumeDetail`、`VolumeReport`、`VolumeTenant`、`VolumeEvent` 等。
- 页面与路由见 FR-12；`AppLayout` 菜单新增「卷管理」组（卷列表 / 租户 / 违规报告 / 回填导入）。
- 列表：Ant Design `Table`，`pagination` 服务端模式；筛选器与搜索同步到 URL query（可分享/刷新保持）。
- 降级弹窗：显示当前卷信息、风险提示，`reason` 必填，提交后刷新。
- 详情：两栏卡片 + `Timeline` 事件流；违规徽标与提示。
- 导入：`Upload`/`TextArea` → 前端解析 CSV 为 JSON → 预览表 → dry-run 结果 → 二次确认后写入。
- 测试：Vitest + Testing Library，覆盖 api 模块、鉴权纯函数与关键交互。

### 6.11 配置

```json
{
  "ConnectionStrings": {
    "VolumeManager": "Host=localhost;Port=5432;Database=opensandbox;Username=...;Password=..."
  },
  "Auth": {
    "Authority": "https://passport.example.com/realms/ai4cbrain",
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

- `Auth:Authority` / `Auth:ClientId` 必填；缺失时启动失败。生产用 `Auth__Authority` / `Auth__ClientId` 环境变量注入。
- `Auth:Authority` 同时用于后端验票、JWKS/discovery 拉取与首页登录域注入；前端不单独配置 realm，避免前后端指向不同 realm。
- K8s 连接：`KubeConfigPath` 为空时使用 in-cluster 配置。
- 启动迁移：`MigrateOnStartup=true` 时执行 EF Core 迁移；生产可用独立 Job 并关闭此项。

### 6.12 部署与权限

#### RBAC

```yaml
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
```

无 `delete`、无 `create`、无 `update`。`patch` 无法在 RBAC 层限制到标签键；代码只 merge §6.1 的四个键（残余风险见 §8）。

#### Keycloak 客户端（部署前置）

在 realm 内创建 public client `opensandbox-web`（与 `Auth:ClientId` 一致）：

| 配置 | 值 |
| --- | --- |
| Client authentication | Off（public client，控制台不保存 secret） |
| Standard flow | On；Direct access grants / Service accounts Off |
| PKCE | S256 必需（Advanced → Proof Key for Code Exchange Code Challenge Method = S256） |
| Valid redirect URIs | `http(s)://<console-host>/*`；开发环境加 `http://localhost:3000/*` |
| Valid post logout redirect URIs | `http(s)://<console-host>/*` |
| Web origins | console origin；开发环境加 `http://localhost:3000`（token 端点需要 CORS） |
| Client roles | `sandbox:admin`、`sandbox:viewer`、`volume:admin`、`volume:viewer` |
| Access token lifespan | 建议 5 分钟（前端提前 60 秒续期） |
| SSO session idle / max | 按组织策略；刷新令牌失效即要求重新登录 |

- 角色分配给用户或组；无角色用户仍能登录，但只能看到无权限页。
- 后端需能访问 `Auth:Authority` 的 discovery / JWKS 端点；浏览器需能访问授权、token 与 logout 端点。
- 客户端创建步骤写入 `deploy/volume-manager/keycloak-client.md`。

#### 部署清单

目录 `deploy/volume-manager/`：

- `rbac.yaml`：ServiceAccount + ClusterRole + ClusterRoleBinding
- `protection-policy.yaml`：VAP + Binding
- `namespace-labels.example.yaml`：受管命名空间打标示例
- `keycloak-client.md`：客户端、角色与 redirect URI 配置步骤
- 数据库：PostgreSQL 实例（外部或 compose 内置），连接串经 Secret 注入

**docker-compose**：新增 `postgres:17` 服务（健康检查 + 持久化卷），backend `depends_on` 健康检查通过后启动。

**健康检查**：`/healthz` 存活；`/readyz` 检查数据库可达（K8s 不可用不阻断 ready——扫描暂停，报告与台账仍可用）；`/api/v1/health` 保留。三者均匿名，供探针与现有脚本使用。

**静态托管**：`UseStaticFiles` 位于鉴权之前；`index.html` 与 SPA fallback 匿名并带 `Cache-Control: no-store`；带 hash 的静态资源可长缓存；`/api/*` 不落到 SPA fallback（返回 JSON 401/403/404）。

### 6.13 上线顺序

| 阶段 | 动作 | 验收 |
| --- | --- | --- |
| P0 前置 | 按 §6.12 创建 Keycloak public client 与角色；给运维分配角色；配置 `Auth:Authority` / `Auth:ClientId` | 管理员能登录；无角色账号只能看到无权限页；匿名调管理 API 返回 401 |
| P0 | 部署 PostgreSQL + 模块 + RBAC；给受管命名空间打标；开启扫描 | 列表与报告可读，未标注卷清单完整 |
| P1 | 创建租户与 Key；业务接入注册新卷；导入存量卷 | 未标注/未注册数量趋近 0，覆盖率可查 |
| P2 | 部署 VAP + Binding，`Warn + Audit` | 审计日志中「本会被挡住」的删除全部是该挡的 |
| P3 | Binding 加入 `Deny` | 误删被拒；租户通过降级端点删除不受影响 |
| P4（可选） | 未标注卷改为 fail-closed | 标签覆盖率稳定接近 100% 后另行决策 |

P2 观察期不可跳过：这是唯一能发现「某个业务仍在合法删除自己的卷、但没走降级端点」的阶段。

---

## 7. 决策记录

### ADR-1 全局模块取代 v4 的单业务组件

**决策**：只保留本模块；v4 的 GoodCrew.VolumeManager 不单独实现，标签泛化为租户中立。

**理由**：两套标签、两个盘点组件会重复且互相矛盾；goodcrew 作为租户接入即可覆盖。

**后果**：goodcrew-pro 需要改为调用本 API（跨仓库后续工作）；VolumeManager 仓库文档仅作参考。

### ADR-2 API 权威，组件代写标签

**决策**：业务调注册 API，台账落库后由组件写 PVC 标签；业务不直接写标签。

**理由**：一次调用完成接入；台账与标签天然一致；标签格式、越权与冲突校验集中在一处。

**后果**：组件需要 PVC `patch` 权限；v4 的「组件严格只读」约束被放弃（但删除能力仍然没有）。K8s 不可用时注册失败，业务必须重试。

### ADR-3 组件永不删除卷

**决策**：不提供删除 API，RBAC 无 `delete`；回收执行留在各业务。

**理由**：平台无法完整理解业务生命周期；goodcrew 的判据已被证明更准确。

**后果**：卷的合法删除路径是「API 降级 → 业务删除」；管理员直接删除会被记为意外消失。

### ADR-4 使用 CEL VAP 而非 Webhook / Finalizer

继承 v4 结论：webhook 有单点与证书问题；finalizer 会让卷卡在 Terminating；CEL 在 apiserver 内执行、无外部依赖。

### ADR-5 非法标签值 fail-closed，未标注 fail-open

- 标签存在但值不认识（含拼错、大小写不符）→ 按 `business` 保护，并记违规。
- 标签缺失 → 不保护，只进报告，驱动回填。

理由与 v4 一致：错误方向必须偏向「多挡一次」；存量未标注卷数量大，立即 fail-closed 会挡住业务现行清理，迫使绕过策略。

### ADR-6 租户自助降级（审计）

**决策**：卷所属租户可以把 `business` 降为 `ephemeral`，必须填 reason；管理员同样可以。

**理由**：数据所有者对自己的数据有处置权；审批组模式会让业务回收流程依赖平台介入；降级后 CEL 自然放行，无需共享审批组。

**后果**：租户内部误操作无法被平台阻止（数据是租户自己的）；所有降级留审计。

### ADR-7 周期扫描落库，而非实时读或 Watch

**决策**：后台 Worker 周期扫描 → PostgreSQL 观测表；API/UI 读库。

**理由**：分页/筛选/排序/历史都需要数据库；K8s 故障不直接影响 UI；实现复杂度远低于 informer。

**后果**：数据新鲜度受 `ScanInterval` 限制；手动扫描作为补充。

### ADR-8 台账与观测分表；名字不是身份

**决策**：`volumes` 存归属，`cluster_volumes` 存当前观测，按 `(namespace, claimName)` 关联；用 UID 识别重建。

**理由**：清单权威在集群，归属权威在台账；同名 PVC 可能被重建，不能按名字迁移归属。

**后果**：同名重建需要租户重新注册；旧台账行保持 `missing` 墓碑。

### ADR-9 消失归因只基于正面证据

**决策**：只有命名空间 list 成功且清单中无该 PVC 才判 missing；list 失败不动台账。

**理由**：一次 API 抖动会把全集群卷标成消失，报告瞬间失真（对应 goodcrew `businessDataLoaded=false` 的教训）。

**后果**：K8s 长期不可用时，消失事件会延迟到恢复后的第一轮扫描。

### ADR-10 PostgreSQL + EF Core

**决策**：台账、观测、审计存 PostgreSQL；EF Core + Npgsql；启动可选迁移。

**理由**：台账是跨租户的权威数据，需要并发安全、备份与审计能力；与 k8s 部署形态匹配。

**后果**：部署新增数据库依赖；docker-compose 与生产环境都需提供实例。

### ADR-11 控制台以 Keycloak 为唯一登录门，不保留 AdminApiKey

**决策**：管理 API 只接受 Keycloak JWT；不实现 `AdminApiKey` 后门。

**理由**：双通道就是绕过 RBAC 的第二条路，且往往没有同等审计。goodcrew-pro 已验证单门（FallbackPolicy + 全局 RBAC）可行；内网不是授权边界。

**后果**：Keycloak 故障时控制台不可登录；删除保护（CEL）、扫描与租户注册 API 不受影响。运维必须维护客户端的 redirect URI 与角色。

### ADR-12 分面角色放在专用客户端的 `resource_access`

**决策**：`opensandbox-web` 客户端角色 `sandbox:admin|viewer`、`volume:admin|viewer`；不读 `realm_access`，不复用 goodcrew-pro 客户端。

**理由**：签发租户 API Key、降级业务卷比日常沙箱运维更敏感，应限制在更小人群；与 goodcrew 的角色语义解耦，两套系统权限互不牵连。

**后果**：新用户需显式分配角色；无角色账号只能看到无权限页；角色变更在 Keycloak 侧审计。

### ADR-13 登录域由后端运行时注入

**决策**：后端把 `Auth:Authority` / `Auth:ClientId` 注入 `index.html` 的 `window.__OPENSANDBOX_AUTH__`；生产前端构建不内置 realm 回落。

**理由**：一个镜像走遍所有环境；避免后端按 A realm 验票、前端把人送去 B realm 登录（表现只是「令牌不对」）。该做法在 goodcrew-pro `AuthConfigInjection` 已验证。

**后果**：`index.html` 必须 `no-store`；注入缺失即启动报错；hash 静态资源仍可长缓存。

---

## 8. 风险与残余风险

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| Keycloak 不可用 | 控制台无法登录（删除保护、扫描与租户注册 API 不受影响） | Keycloak 高可用；监控 discovery 端点；不接受匿名降级 |
| 控制台 token 存 `sessionStorage` | XSS 可窃取短时令牌 | 严格 CSP；不引入不可信脚本；access token 短时效 + 刷新令牌会话级 |
| 角色配置错误 | 403 过严，或某面权限授予过宽 | 角色少而粗；无角色 fail-closed；Keycloak 侧变更走审计 |
| 首页注入缺失 | 前端无法登录或指向错误 realm | 生产构建无 realm 回落，启动即报错；部署冒烟检查 `index.html` |
| 组件有 PVC `patch` 权限 | 理论上可改任意 PVC 标签 | 代码只 patch 四个契约键；审查与最小权限；后续可用 VAP 限制组件自身的 patch 范围 |
| 删除命名空间级联 | 绕过 PVC admission | 批量消失检测 + 报告；用 RBAC 限制 `namespaces` 删除 |
| 直接删除 PV | PV 不继承 PVC 标签 | `reclaimPolicy: Retain`；限制 PV 删除权限 |
| 移除标签后删除 | 逃生通道 | 审计日志 + 漂移事件 + 消失归因 |
| 租户 Key 泄漏 | 可注册/降级该租户的卷 | Key 可吊销；哈希存储；日志脱敏；按租户隔离 |
| 业务忘记注册 | 卷不受保护 | 未标注/未注册报告与覆盖率；P2 观察期 |
| 标签漂移 | 保护状态与台账不一致 | 每轮扫描对账；漂移事件；详情页展示 |
| K8s API 抖动 | 消失误判 | ADR-9 的正面证据规则 |
| 数据库不可用 | 注册/管理不可用 | 503 明确报错；保护不受影响（CEL） |
| CEL 与分类器不一致 | 盘点结论与实际保护相反 | FR-2 要求同一组用例在单测与 kind 两侧都测 |

### 假设

| 编号 | 假设 |
| --- | --- |
| A-1 | 控制台接入 Keycloak；realm 管理员按 §6.12 创建 public client 与角色；浏览器与后端均能访问 Keycloak |
| A-2 | 集群 Kubernetes ≥ 1.30 |
| A-3 | 业务后端能访问本服务（集群内 Service 或内网地址） |
| A-4 | OpenSandbox server 创建 PVC 是同步的，注册时 PVC 已存在（否则 `not-found` 重试） |
| A-5 | 控制台主机与 Keycloak 时钟偏差在 30 秒容差内（NTP） |

---

## 9. 验收标准

| 需求 | 验证方式 | 通过条件 |
| --- | --- | --- |
| FR-1 | 单测 + 接口测试 | 创建/鉴权/吊销/跨租户拒绝全部符合 |
| FR-2 | 单测 + kind e2e | 四种输入类别行为与表一致；非法值被保护 |
| FR-3 | 服务层测试 | 冲突矩阵八行逐行通过 |
| FR-4 | 接口测试 + kind e2e | 缺 reason 被拒；降级后可删；审计可查 |
| FR-5 | fake `IKubernetes` | 四类场景通过 |
| FR-6 | 纯函数测试 + 集成测试 | §6.7.2 状态迁移逐行通过 |
| FR-7 | kind e2e | 非审批组被拒、审批组成功；停组件后仍生效 |
| FR-8 | 接口测试 | 分页语义六条全部通过 |
| FR-9 | 接口测试 | dry-run 无写入；逐条结果；不覆盖冲突 |
| FR-10 | 快照测试 | 结构与计数正确；截断标记正确 |
| FR-11 | 单测 | 每个迁移都有事件 |
| FR-12 | Vitest | 五个页面关键交互通过 |
| FR-13 | 接口测试 + Vitest | §6.4 矩阵逐格；`azp`/issuer/过期/无 token → 401；租户 Key 与 JWT 互不越界；登录、续期、退出与无权限页通过 |
| NFR-1 | 停机测试 | 保护仍生效 |
| NFR-2 | 清单审查 | ClusterRole 无 delete |
| NFR-3 | 性能测试 | 约定规模内扫描 ≤ 4 分钟 |
| NFR-5 | 故障注入 | 依赖不可用返回 503，不返回空清单 |
| NFR-9 | RBAC 矩阵测试 | 未匹配 `/api` 拒绝；匿名 401、角色不足 403 |
| NFR-10 | 注入测试 + 打包审查 | 生产构建无 realm 回落；配置缺失启动失败 |

---

## 10. 测试策略

| 层 | 手段 |
| --- | --- |
| 分类器 | 纯函数单测：缺失 / 空白 / `ephemeral` / `business` / 拼错 / 大写 / 未知版本 / 空白裁剪 |
| 分类器与 CEL 一致性 | 同一组用例分别跑单测与 kind 集群实际拒绝行为 |
| 对账 | 纯函数单测：§6.7.2 每一行、批量阈值边界、命名空间 404/unmanaged、同名重建 |
| 扫描器 | fake `IKubernetes`：多命名空间、Pod 挂载、已结束 Pod、单命名空间 list 失败、namespace 404 |
| 注册服务 | fake 仓储 + fake K8s：冲突矩阵、标签 patch 失败、not-found、跨租户 |
| API | `WebApplicationFactory` + Testcontainers PostgreSQL |
| CEL | kind（≥1.30）：审批组内/外、business/ephemeral/非法值、未打标命名空间 |
| 控制台鉴权 | `WebApplicationFactory` + 测试签发：issuer、`azp`、过期、无 token、角色矩阵逐格 |
| 身份互斥 | 租户 Key 调管理 API → 401（管理 API 只接受 Keycloak JWT）；控制台 JWT 调租户 API → 401 |
| 登录域注入 | 单元测试：`</head>` 注入、JSON 转义、缺少 head 抛错 |
| 前端 | Vitest + Testing Library：api 模块、列表筛选分页、详情时间线、导入 dry-run |
| 前端鉴权 | Vitest：`authModel`、PKCE S256、state 不匹配、401 重登、角色门控 |

### 必须覆盖的边界

`lifecycle` 缺失/空白/大写/拼错；owner 标签超长省略；命名空间列表失败不判消失；页码越界；搜索跨页；降级无 reason；Key 吊销后请求；导入冲突项不覆盖；`azp` 不符；无角色账号；吊销的租户 Key 调管理 API；生产构建缺少登录域注入。

---

## 11. PR 拆解

| # | PR | 内容 | 依赖 |
| --- | --- | --- | --- |
| 1 | `feat(auth): backend console authentication` | JwtBearer（`azp`）、角色解析、RBAC 矩阵、`/console/me`、登录域注入、现有端点测试改用测试身份 + 鉴权测试 | — |
| 2 | `feat(auth): frontend login & role gates` | `src/auth/*`、启动门控、axios 拦截、用户菜单、菜单/按钮门控、无权限页、设置页账号信息 + Vitest | 1 |
| 3 | `feat(volumes): contracts & classifier` | 标签常量、分类器、对账纯函数 + 单测（零外部依赖） | — |
| 4 | `feat(volumes): persistence` | EF Core 实体、DbContext、迁移、仓储 + 测试 | 3 |
| 5 | `feat(volumes): inventory scanner` | K8s 客户端、扫描 Worker、对账落库、标签重试 + fake `IKubernetes` 测试 | 3, 4 |
| 6 | `feat(volumes): tenant api` | Key 鉴权、注册、lifecycle 变更、标签 patch + 测试 | 1, 3–5 |
| 7 | `feat(volumes): admin api` | 列表/详情/报告/导入/扫描、租户与 Key 管理、角色策略 + 测试 | 1, 4–6 |
| 8 | `feat(volumes): protection manifests` | VAP/Binding/RBAC/命名空间打标 + kind e2e | —（YAML 独立；e2e 需 kind） |
| 9 | `feat(volumes): admin ui` | api 模块、五个页面、路由与菜单、Vitest | 2, 7 |
| 10 | `docs(volumes): operations guide` | 接入指南、Keycloak 客户端步骤、运维手册、事故处置（意外消失排查） | 全部 |

PR 2 与 PR 3 可并行；PR 1 必须先于 6/7 合并，否则管理 API 测试要重写两遍。PR 8 可与其余并行。PR 4/5 可并行开发但 5 依赖 4 的实体。

---

## 12. 附录

### A. 与 goodcrew-pro 的对应关系

| goodcrew-pro | 本模块 |
| --- | --- |
| `SandboxVolumeBindings`（租户列 + owner + role + claimName） | `volumes` 台账（TenantId + OwnerKind/OwnerId/Role + Namespace/ClaimName） |
| `UpsertAsync` 幂等登记、归属不可改 | 注册冲突矩阵的 `updated` 与 `conflict` |
| `LastSandboxId` 取证线索 | 同名保留 |
| `VolumeReclaimPolicy`（白名单、Decide/AutoReclaimable 分离） | **不实现**；回收仍在业务侧 |
| `VolumeInventoryService` 三路并行盘点 | `InventoryScanner` |
| `businessDataLoaded=false` 整批停手 | 命名空间 list 失败的「不迁移」规则 |
| `VolumeFact.Uid` + 删除前 UID 前置条件 | `RegisteredUid` / 同名重建检测 |
| 分页筛选→搜索→排序→切片的教训 | FR-8 列表语义 |
| `VolumeInventoryService` 单命名空间 | 多命名空间、多租户 |
| `AuthExtensions`（JwtBearer + `azp` + FallbackPolicy） | §6.4 控制台 JWT 校验 |
| `Rbac.RolesOf`（`resource_access.<clientId>.roles`） | §6.4 角色解析 |
| `Rbac.UseTrainerRbac`（全局默认拒绝） | `ConsoleRbac` |
| `AuthConfigInjection`（登录域注入首页） | 同名 `AuthConfigInjection` |
| `frontend/src/shared/auth/*`（PKCE、sessionStorage、静默续期） | `frontend/src/auth/*` |

### B. 与 v4 的差异

| v4 | 本 spec |
| --- | --- |
| 独立 GoodCrew.VolumeManager 组件 | OpenSandBoxWeb 内的模块 |
| `goodcrew/lifecycle` 等业务标签 | `opensandbox.io/volume-*` 租户中立标签 |
| 组件只读（get/list/watch） | 组件可 patch 标签（注册代写） |
| 无数据库 | PostgreSQL 台账 + 观测 + 审计 |
| 回填 CLI | 管理 API + UI 导入 |
| 单业务 | 多租户 |
| `owner` 示例用冒号（K8s 非法） | `<ownerKind>-<ownerId>` 规范化 |
| 不变 | CEL 保护、永不删除、非法值 fail-closed、未标注 fail-open、权威源不可读不动 |

### C. 台账状态机

```text
active ──成功 list 且不在清单（ephemeral）──→ missing (ephemeral-gone)
active ──成功 list 且不在清单（business）──→ missing (unexpected)
active ──从未观测且不在清单───────────────→ missing (never-observed)
active ──命名空间 404────────────────────→ missing (namespace-deleted)
active ──命名空间失去受管标签────────────→ unmanaged
missing ──租户重新注册（PVC 存在）───────→ active
unmanaged ──命名空间重新受管（PVC 存在）─→ active
```

### D. 事件与状态迁移对照

| 迁移 | 事件 |
| --- | --- |
| 新建台账 | `registered` |
| 重复注册更新 | `updated` |
| missing → active | `reactivated` |
| 从未观测 → missing | `missing-before-observed` |
| unmanaged → active | `namespace-managed-again` |
| 注册冲突 | `registration-conflict` |
| 标签写入 | `label-applied` / `label-patch-failed` |
| 外部改标签后被纠正 | `label-corrected` |
| lifecycle 变更 | `lifecycle-changed` |
| 违规集合变化 | `drift-detected` / `drift-resolved` |
| 消失（ephemeral） | `disappeared` |
| 消失（business） | `unexpected-disappearance` |
| 批量消失 | `namespace-mass-disappearance` |
| 失去受管 | `namespace-unmanaged` |
| 同名重建 | `reappeared-with-new-uid` |
| 删除中 | `terminating-observed` |
| 导入 | `imported` |
