# OpenSandBoxWeb 前后端结构改造设计

日期：2026-10-25
状态：待评审

## 1. 背景与目标

OpenSandBoxWeb 目前是一个纯前端项目：Vue 3 + Vite + Element Plus 的 OpenSandbox 管理控制台，直接对接 OpenSandbox Lifecycle API（localhost:8080/v1）。

本次改造目标：参照 [goodcrew-pro](E:\GitHub\goodcrew-pro) 的前后端分离结构（`frontend/` + `backend/` + `tests/`），将仓库改造为：

- **后端**：.NET 10 ASP.NET Core API，重写沙箱管理 API，通过 NuGet 包 `Alibaba.OpenSandbox` 1.1.1（阿里官方 C# SDK）与 OpenSandbox 平台交互
- **前端**：迁移到 React 19 + Vite + Ant Design（与 goodcrew-pro 前端技术栈对齐），功能与现有 7 个视图 1:1 等价
- **结构深度**：适度参照 goodcrew-pro —— frontend/backend 拆分、backend 单 csproj 按 Host/Infrastructure 分层、tests 测试项目、Dockerfile + docker-compose；不引入 helm/k8s 等重型部署件

## 2. 关键决策

| 决策点 | 结论 | 理由 |
|--------|------|------|
| 后端角色 | 重写管理 API（非纯 BFF 代理） | 用户明确选择 |
| 后端代码来源 | 使用 `Alibaba.OpenSandbox` 1.1.1 NuGet SDK | 用户明确选择；SDK 已确认覆盖全部所需能力（见 §4.2） |
| 前端技术栈 | 迁移到 React 19 + Ant Design | 用户明确选择，对齐 goodcrew-pro |
| 数据库 | 无 | 沙箱状态以平台 + SDK 为准；不引入 EF Core/Migrations（YAGNI） |
| 结构参照深度 | 适度 | 用户明确选择 |
| 认证 | 无 | 现有应用无登录，保持现状（YAGNI） |

## 3. 目标仓库结构

```
OpenSandBoxWeb/
├── backend/                      # .NET 10 API（单 csproj，Host/Infrastructure 分层）
│   ├── OpenSandbox.Web.csproj    # net10.0 + Alibaba.OpenSandbox 1.1.1
│   ├── Program.cs                # 组装入口（扩展方法，仿 goodcrew-pro）
│   ├── appsettings.json / appsettings.Development.json
│   ├── Contracts/                # API DTO（请求/响应模型）
│   ├── Host/
│   │   ├── Configuration/        # OpenSandbox 连接配置绑定
│   │   └── Web/                  # SandboxEndpoints.cs、MetricsEndpoints.cs、
│   │                             # HealthEndpoints.cs、SpaFallbackEndpoints.cs
│   └── Infrastructure/
│       ├── DependencyInjection/  # SDK 注册扩展
│       └── Gateways/             # SandboxGateway（包装 SDK）+ 错误→HTTP 状态翻译
├── frontend/                     # React 19 + Vite + Ant Design（Vue 代码全部重写）
│   ├── src/
│   │   ├── api/                  # axios 实例 + sandbox.ts + metrics.ts + types.ts
│   │   ├── components/           # common/ + layout/
│   │   ├── hooks/                # usePolling 等
│   │   ├── pages/                # 7 个视图
│   │   ├── router/
│   │   ├── styles/
│   │   └── utils/
│   ├── vite.config.ts            # dev proxy: /api → http://localhost:5080
│   └── package.json
├── tests/
│   └── OpenSandbox.Web.Tests/    # xUnit
├── docs/
├── screenshots/                  # 保留并更新
├── Dockerfile                    # 多阶段：node 构建前端 → dotnet 发布 → 运行期托管静态文件
├── docker-compose.yml
├── .dockerignore
├── .gitignore
└── README.md                     # 双语重写（结构图 + 技术栈 + API 表）
```

## 4. 后端设计

### 4.1 项目与分层

- `backend/OpenSandbox.Web.csproj`：`Microsoft.NET.Sdk.Web`，`net10.0`，Nullable + ImplicitUsings
- 分层仿 goodcrew-pro 模式：
  - `Program.cs` 只做组装，通过扩展方法调用（如 `AddOpenSandboxGateway(builder.Configuration)`、`MapSandboxEndpoints()`）
  - `Host/Web/` 下按功能拆端点文件，每个文件一个 `MapXxxEndpoints(this WebApplication app)` 扩展方法
  - `Infrastructure/DependencyInjection/` 放服务注册扩展
  - `Infrastructure/Gateways/` 放 SDK 包装与错误翻译
  - `Contracts/` 放 API 请求/响应 DTO（与 SDK 模型解耦）
- 监听 `http://localhost:5080`（launchSettings/appsettings 可配）

### 4.2 SDK 集成

`Alibaba.OpenSandbox` 1.1.1 关键 API（已通过反射确认）：

- `ConnectionConfigOptions` → `ConnectionConfig`：Domain、ApiKey、Protocol、RequestTimeoutSeconds、EndpointCache 等
- `DefaultAdapterFactory.CreateLifecycleStack(options)` → `LifecycleStack.Sandboxes`（`ISandboxes`）：
  `CreateSandboxAsync`、`GetSandboxAsync`、`ListSandboxesAsync`、`DeleteSandboxAsync`、
  `PauseSandboxAsync`、`ResumeSandboxAsync`、`RenewSandboxExpirationAsync`、
  `GetSandboxEndpointAsync(id, port, ...)`、`PatchSandboxMetadataAsync`、快照/模板管理
- `DefaultAdapterFactory.CreateExecdStack(options)` → `ExecdStack.Metrics.GetMetricsAsync()`、`ExecdStack.Health.PingAsync()`

SDK 模型与现有前端契约字段对齐：`CreateSandboxRequest`（Image、Entrypoint、Timeout、ResourceLimits、Env、Metadata、NetworkPolicy、Volumes、Extensions）、`SandboxInfo`（Id、Image、Entrypoint、Metadata、Status、CreatedAt、ExpiresAt）、`ListSandboxesParams`（States、Metadata、Page、PageSize）、`SandboxMetrics`（CpuCount、CpuUsedPercentage、MemoryTotalMiB、MemoryUsedMiB、Timestamp）。

DI 注册方案：

```csharp
services.AddSingleton<DefaultAdapterFactory>();
services.AddSingleton(sp => new ConnectionConfig(sp.GetRequiredService<IOptions<OpenSandboxOptions>>().Value.ToConnectionConfigOptions()));
// Gateway 层按需创建 LifecycleStack / ExecdStack
```

配置绑定（appsettings.json）：

```json
{
  "OpenSandbox": {
    "Domain": "https://...",
    "ApiKey": "...",
    "RequestTimeoutSeconds": 30
  }
}
```

### 4.3 API 面（路径统一到 `/api/v1`）

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/v1/health` | GET | 健康检查（SDK HealthAdapter.PingAsync） |
| `/api/v1/sandboxes` | GET | 列表（分页/状态/metadata 过滤） |
| `/api/v1/sandboxes` | POST | 创建沙箱 |
| `/api/v1/sandboxes/{id}` | GET | 详情 |
| `/api/v1/sandboxes/{id}` | DELETE | 删除 |
| `/api/v1/sandboxes/{id}/pause` | POST | 暂停 |
| `/api/v1/sandboxes/{id}/resume` | POST | 恢复 |
| `/api/v1/sandboxes/{id}/renew-expiration` | POST | 延长过期时间 |
| `/api/v1/sandboxes/{id}/endpoints/{port}` | GET | 获取沙箱端口端点 |
| `/api/v1/sandboxes/{id}/metrics` | GET | 指标快照（ExecdStack.Metrics） |
| `/api/v1/sandboxes/{id}/metrics/watch` | GET | SSE 实时指标（后端 2s 轮询推送） |

设计说明：

- **指标走后端代理**：前端不再直连沙箱动态端点（`http://{endpoint}/metrics/watch`），改为后端 SSE 推送，消除浏览器 CORS 问题，沙箱端点不再暴露给浏览器
- **错误统一**：SDK `SandboxApiException`/`SandboxErrorCodes` → HTTP 状态码（400 参数/409 冲突/404 不存在/503 不健康/504 就绪超时），响应体统一 `{code, message}`
- **DTO 解耦**：Contracts 层定义 Web API 自己的请求/响应模型，Gateway 负责 SDK 模型 ↔ DTO 映射，避免 SDK 模型直接暴露给前端

### 4.4 生产静态托管

生产模式下后端托管 `frontend/dist` 静态文件，SPA fallback 到 index.html（仿 goodcrew-pro `SpaFallbackEndpoints`），实现单进程部署。

## 5. 前端设计（React 重写）

### 5.1 技术栈

| 技术 | 用途 |
|------|------|
| React 19 | 框架 |
| react-router-dom 7 | 路由 |
| Ant Design 5 + @ant-design/icons | UI 组件 |
| axios | HTTP 客户端 |
| @tanstack/react-query | 服务端状态（替代 Pinia stores） |
| echarts 6（自写轻量 React 封装组件，不引第三方 wrapper） | 图表 |
| dayjs | 日期格式化 |
| TypeScript strict | 类型安全 |
| Vite | 构建（dev 端口 3000，proxy `/api` → 5080） |
| vitest + @testing-library/react | 测试 |
| oxlint | lint（与 goodcrew-pro 一致） |

### 5.2 功能 1:1 对齐（7 个视图）

| 现有 Vue 视图 | React 页面 | 功能 |
|---------------|-----------|------|
| Dashboard.vue | pages/Dashboard | 状态统计卡、快捷操作、最近创建列表 |
| SandboxList.vue | pages/SandboxList | 状态筛选、关键词搜索、分页、30s 自动刷新、批量删除 |
| SandboxDetail.vue | pages/SandboxDetail | 完整配置信息、实时指标面板（SSE）、暂停/恢复/删除、TTL 续期 |
| CreateSandbox.vue | pages/CreateSandbox | 镜像/名称/入口命令、CPU/内存限制、超时、环境变量、网络策略 |
| BatchCreate.vue | pages/BatchCreate | JSON 导入、手动添加、并发控制、进度显示 |
| Settings.vue | pages/Settings | 应用设置 |
| NotFound.vue | pages/NotFound | 404 |

### 5.3 状态管理迁移

- 沙箱列表/详情/指标：React Query（缓存 + 自动刷新 interval）
- 通知：antd App context 的 message/notification
- 轮询：`hooks/usePolling` 封装 React Query `refetchInterval`
- UI 局部状态（筛选、表单）：useState/useForm

## 6. 测试

### 6.1 后端（xUnit）

- Gateway 错误翻译：各 SandboxErrorCode → HTTP 状态映射
- 端点参数校验：非法 page/pageSize、缺少必填字段
- SDK 接口以 mock 注入（`ISandboxes` 等为接口，可 mock）

### 6.2 前端（vitest + Testing Library）

- 创建表单校验规则
- 批量创建进度与结果汇总
- 列表筛选/分页交互

## 7. 开发与部署

### 7.1 开发工作流

```bash
# 终端 1：后端
cd backend && dotnet run          # http://localhost:5080

# 终端 2：前端
cd frontend && npm install && npm run dev   # http://localhost:3000，/api 代理到 5080
```

### 7.2 生产部署

- 单条多阶段 Dockerfile：node 构建 frontend → dotnet publish backend → runtime 镜像托管静态文件
- 静态产物接线：前端构建到 `frontend/dist`，后端 csproj 用 `<Content Include="..\frontend\dist\**" CopyToOutputDirectory="PreserveNewest">` 将产物纳入发布目录（Docker 阶段亦按此路径 COPY），SPA fallback 从该目录服务
- docker-compose.yml 提供单命令本地起完整环境

### 7.3 文档

- README 双语重写：技术栈表、仓库结构图、API 表、开发/构建说明
- screenshots 更新为新 UI

## 8. 非目标（YAGNI）

- 无登录/权限（现有应用无认证，保持现状）
- 无数据库/Migrations
- 无 helm/k8s manifests
- 无 OpenTelemetry/MCP/A2A 集成
- 无 CI workflow（后续按需添加）
- 不迁移 Vue 代码（git 历史保留，重写为 React）

## 9. 实施顺序（供后续计划参考）

1. 仓库骨架：frontend/backend/tests 目录、.gitignore、根级文件
2. 后端：csproj + SDK 集成 + Gateway + 全部端点 + 测试
3. 前端：脚手架 + 基础布局 → 列表/详情 → 创建/批量创建 → 仪表盘/设置
4. 静态托管 + Dockerfile + docker-compose
5. README/screenshots 更新
