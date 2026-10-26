# VolumeManager 运维手册

## 1. 组件

| 组件 | 作用 |
| --- | --- |
| PostgreSQL 17 | 台账、租户、Key、扫描与事件 |
| VolumeManager 模块 | 扫描集群 PVC、对账、标签补写、报表与 API |
| Keycloak `opensandbox-web` | 控制台登录与四个分面角色 |
| Kubernetes RBAC / VAP | 只读扫描 + 标签 patch + 业务卷删除保护 |
| 租户 API Key | 业务侧注册/降级卷，不依赖 Keycloak |

配置项见 `docs/superpowers/specs/2026-10-25-global-volume-manager-design.md` §6.11：
`ConnectionStrings:VolumeManager`、`VolumeManager:Enabled`、`VolumeManager:ScanInterval`、
`VolumeManager:KubeConfigPath`、`Auth:*`。

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

`VolumeManager:Enabled=true` 且 `MigrateOnStartup=true` 时后端启动执行 EF 迁移；生产也可用独立 Job。
备份与恢复（宿主机，compose 已把 5432 发布到 127.0.0.1；先 `export POSTGRES_PASSWORD=<.env 中的值>`）：

```bash
PGPASSWORD=$POSTGRES_PASSWORD pg_dump -h 127.0.0.1 -U opensandbox -Fc -d opensandbox -f volume-manager.dump
PGPASSWORD=$POSTGRES_PASSWORD pg_restore -h 127.0.0.1 -U opensandbox -d opensandbox --clean --if-exists volume-manager.dump
```

或在容器内执行：

```bash
docker compose exec -T postgres pg_dump -U opensandbox -Fc -d opensandbox > volume-manager.dump
docker compose exec -T postgres pg_restore -U opensandbox -d opensandbox --clean --if-exists < volume-manager.dump
```

不要手工改台账归属；冲突应通过重新注册或管理 API 处理。

Compose 场景注意：compose 不是 Kubernetes 主机，`opensandbox-web` 容器内没有可用的
Kubernetes API（没有 in-cluster ServiceAccount，也没有挂载 kubeconfig），保持
`VOLUME_MANAGER_ENABLED=true` 只会让扫描器每一轮失败并在日志中反复报错（见 §7）。

但 schema 迁移绑定在 `Enabled && MigrateOnStartup` 上，**全新数据库不能靠关闭模块来建表**。
要求的顺序：

1. 首次启动保持 `VOLUME_MANAGER_ENABLED=true`（或对 `ConnectionStrings:VolumeManager`
   执行 `dotnet ef database update` / 独立的迁移 Job），让表结构存在。
2. 确认表已创建后，才可以关闭扫描（`VOLUME_MANAGER_ENABLED=false`）。关闭后租户注册与
   只读路径（台账、报告、租户与 Key 管理）仍然可用，只是不会更新集群观测数据。

全新数据库在模块关闭时没有任何表，卷相关端点会返回 `503 dependency_unavailable`。

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

这些 `--as` 检查只验证 ClusterRoleBinding 是否生效，不代表后端 Pod 真的以该身份运行。
集群内运行时，后端 Deployment 必须设置 `serviceAccountName: opensandbox-volume-manager`
（并部署在 `rbac.yaml` 中 ServiceAccount 所在的命名空间）；集群外运行时，改为设置
`VolumeManager:KubeConfigPath` 指向可用的 kubeconfig。

## 5. 受管命名空间

```bash
kubectl label namespace opensandbox opensandbox.io/volume-managed=true
kubectl get namespaces -l opensandbox.io/volume-managed=true
```

移除标签会让该命名空间的台账进入 `unmanaged`，不删除任何 PVC。

## 6. 租户接入

1. 控制台「租户管理」新建租户，填写租户 `id`、显示名与命名空间。`id` 必须满足
   `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`（小写字母、数字与连字符，首尾为字母或数字，最长
   63 字符），并且就是导入 CSV 中 `tenant` 列使用的值，创建后不可修改。
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

若 PVC 标签 patch 失败，降级响应为 `label-pending`：台账中的 lifecycle 已是 `ephemeral`，
但 PVC 上仍是原来的 `opensandbox.io/volume-lifecycle` 标签，删除会继续被 VAP 拒绝。
下一轮扫描会重试标签写入，成功后 `LabelState` 变为 `applied`，删除才会放行；可在卷详情
的事件时间线中确认重试结果。

5. 查看自己的卷：`GET /api/v1/tenant/volumes?page=1&pageSize=20`（强制租户过滤）。

Key 轮换：签发新 Key → 业务切换 → 确认旧 Key 的 `LastUsedAt` 不再变化 → 吊销旧 Key。
`LastUsedAt` 最多每 5 分钟更新一次。

## 7. 扫描与报告

- 后台每 `ScanInterval`（默认 5 分钟）扫描一次；`ScanOnStartup` 控制启动扫描。
- 控制台「立即扫描」是同步请求；已有扫描时返回 `409 scan_in_progress`。
- 某命名空间 list 失败时，该命名空间本轮不迁移任何状态，错误写入 `scan_runs."Errors"`。
- K8s 不可用不阻断 `/readyz`；台账与历史报告仍可读。
- `VolumeManager:Enabled=false` 时扫描器不启动、不产生新的扫描记录；前提是 schema 已由
  启用状态下的启动迁移或独立迁移 Job 建好，此时租户注册与读取路径仍可用。全新数据库
  必须先执行迁移（见 §3）。

常用 SQL（只读；EF 迁移创建的是带引号的 PascalCase 列名）：

```sql
select "State", "StateReason", count(*) from volumes group by 1, 2 order by 3 desc;
select classification, count(*) from (
  select case when "LifecycleLabel" is null then 'unlabeled' else 'labeled' end as classification
  from cluster_volumes
) t group by 1;
select "StartedAt", "NamespacesScanned", "NamespacesSucceeded", "VolumeCount"
from scan_runs order by "StartedAt" desc limit 10;
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

- DELETE 准入请求没有 `object`，策略的 CEL 只读取 `oldObject.metadata.labels`。因此保护条件是
  「被删除 PVC 的现有标签中存在 `opensandbox.io/volume-lifecycle`，且值不为 `ephemeral`」。
  未打 lifecycle 标签的卷默认**不**保护；已经降级为 `ephemeral` 的卷允许删除。
- 策略只在 Binding 匹配的命名空间生效：命名空间必须带
  `opensandbox.io/volume-managed: "true"` 标签（见 §5 与
  `deploy/volume-manager/namespace-labels.example.yaml`）。未打该标签的命名空间中，即使
  PVC 带业务 lifecycle 标签也不会被拦截。
- `Deny` 生效后，受管命名空间中的 `deletecollection` / 按标签选择器的批量删除是 fail-closed：
  集合删除不携带 old object，表达式无法批准这类请求，因此会被拒绝。运维删除请逐个删除
  单个 PVC，或使用审批组身份。
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
| `503 dependency_unavailable` | PostgreSQL 是否可达；schema 是否已迁移；K8s API 是否可达 |
| 扫描一直失败 | `scan_runs."Errors"`；ServiceAccount 是否可 list PVC/Pod；命名空间标签 |
| Compose 中扫描器反复报错 | Compose 环境没有 K8s API；先以 `VOLUME_MANAGER_ENABLED=true` 完成迁移，再设为 `false`（见 §3） |
| 卷显示 `unmanaged` | 命名空间是否移除受管标签；重新打标后下轮扫描恢复 |
| 状态 `label-pending` | K8s patch 权限；下轮扫描自动重试 |
| 导入全 `invalid` | 命名空间是否分配给租户；是否打受管标签 |
| 误删无法执行 | P3 已启用；先走降级端点，或由审批组操作；批量删除会被 fail-closed 拒绝，改为逐个删除 |
