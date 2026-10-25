# OpenSandbox Admin

[English](#english) | [中文](#中文)

---

<a name="english"></a>
## English

### Overview

OpenSandbox Admin is a modern web-based management console for **OpenSandbox** - a lightweight, secure sandbox environment orchestration platform. This admin dashboard provides a comprehensive interface for managing containerized sandbox instances, monitoring resource usage, and controlling sandbox lifecycles.

The repository follows a frontend/backend split: a React SPA and a .NET 10 API that wraps the official [Alibaba.OpenSandbox](https://www.nuget.org/packages/Alibaba.OpenSandbox) SDK.

### Features

- **Dashboard Overview** - Real-time statistics of sandbox states (Running, Paused, Creating, Error)
- **Sandbox Management** - Create single or batch sandboxes from container images; pause, resume and delete instances; filter and search by state or metadata
- **Real-time Metrics** - Live CPU and memory monitoring streamed over SSE through the backend proxy
- **TTL Management** - Extend sandbox expiration with a datetime picker
- **Batch Operations** - Create multiple sandboxes via JSON import or manual input with progress tracking
- **Single-process Deployment** - Backend serves the built frontend with SPA fallback
- **Keycloak SSO** - Console login through Keycloak with separate sandbox and volume admin/viewer roles

### Tech Stack

| Area | Technology |
|------|------------|
| Frontend | React 19, TypeScript, Vite, Ant Design 5, TanStack Query, ECharts, axios, dayjs |
| Backend | .NET 10, ASP.NET Core Minimal APIs, Alibaba.OpenSandbox 1.1.1 |
| Tests | xUnit + WebApplicationFactory (backend), Vitest + Testing Library (frontend) |

### Repository Structure

```
├── backend/       # .NET 10 API (Contracts / Host / Infrastructure layers)
├── frontend/      # React 19 + Vite + Ant Design SPA
├── tests/         # xUnit test project
├── Dockerfile     # Multi-stage: node build -> dotnet publish -> runtime
└── docker-compose.yml
```

### Getting Started

Prerequisites: .NET SDK 10.0.401+, Node.js 20.19+ or 22.12+

```bash
# Terminal 1 - backend (http://localhost:5080)
cd backend
dotnet run

# Terminal 2 - frontend dev server (http://localhost:3000, proxies /api to 5080)
cd frontend
npm install
cp .env.example .env.local   # Keycloak realm/client for the Vite dev server
npm run dev
```

### Configuration

Backend settings in `backend/appsettings.json`:

```json
{
  "OpenSandbox": {
    "Domain": "https://sandbox.example.com",
    "ApiKey": "your-api-key"
  }
}
```

When running under Docker, compose overrides the same settings with the `OpenSandbox__Domain` / `OpenSandbox__ApiKey` environment variables (see `.env.example`).

Console authentication uses the `Auth` section and is required:

```json
{
  "Auth": {
    "Authority": "https://keycloak.example.com/realms/your-realm",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  }
}
```

The backend refuses to start when `Auth:Authority` or `Auth:ClientId` is missing. The Keycloak client is public, uses Standard Flow with PKCE S256, and must register the console origin (for example `http://localhost:3000/*` in development and `http://<console-host>:5080/*` in Docker) as a redirect URI and web origin. Register the same origin under **Valid post logout redirect URIs** (for example `http://localhost:3000/*` and `http://<console-host>:5080/*`): the console always sends `post_logout_redirect_uri`, and Keycloak shows an error page after logout when that URI is not registered.

### API

All endpoints are prefixed with `/api/v1`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Liveness check |
| `/console/me` | GET | Current console identity and roles |
| `/sandboxes` | GET | List sandboxes (pagination, state/metadata filters) |
| `/sandboxes` | POST | Create a sandbox |
| `/sandboxes/{id}` | GET | Sandbox details |
| `/sandboxes/{id}` | DELETE | Delete a sandbox |
| `/sandboxes/{id}/pause` | POST | Pause a sandbox |
| `/sandboxes/{id}/resume` | POST | Resume a sandbox |
| `/sandboxes/{id}/renew-expiration` | POST | Extend expiration time |
| `/sandboxes/{id}/endpoints/{port}` | GET | Get sandbox endpoint for a port |
| `/sandboxes/{id}/metrics` | GET | Metrics snapshot |
| `/sandboxes/{id}/metrics/watch` | GET | Live metrics over SSE |

Errors return a uniform body: `{ "code": "...", "message": "..." }`.

### Docker

```bash
# OpenSandbox settings work as-is: compose starts the web app (localhost:5080) and the built-in opensandbox-server (127.0.0.1:8090).
# AUTH_AUTHORITY is mandatory: only the example .env supplies the project's shared dev realm.
cp .env.example .env
docker compose up -d
```

- `opensandbox-web` reaches `opensandbox-server` over the compose network; the OpenSandbox defaults require no `.env` changes. Set `OPENSANDBOX_DOMAIN` / `OPENSANDBOX_API_KEY` only when connecting to an external OpenSandbox platform.
- `AUTH_AUTHORITY` is mandatory (`docker compose` fails fast without it). The example `.env` points at the project's shared dev realm `https://dev-passport.ai4c.cn/realms/ai4cbrain`, which is dev-only: production must use its own realm and register the console origin for both the redirect URI and the post logout redirect URI.
- `opensandbox-server` mounts the host's `/var/run/docker.sock` to create sandbox containers (inherent to the upstream design), so it can see and clean up expired sandbox containers labelled OpenSandbox on the host; port 8090 is bound to 127.0.0.1 only.
- Pre-pull the runtime images `opensandbox/execd:v1.1.0` and `opensandbox/egress:v1.1.7` before creating the first sandbox: a cold pull may exceed the backend's default 30s request timeout (raise OPENSANDBOX_REQUEST_TIMEOUT_SECONDS in .env).
- After editing the inline server configuration in `docker-compose.yml`, run `docker compose up -d --force-recreate opensandbox-server` for the change to take effect.
- The console now requires a Keycloak login. Register the console redirect URIs, web origins and post logout redirect URIs in the `opensandbox-web` client before exposing port 5080; the API rejects anonymous requests even if the UI is bypassed.

### Testing

```bash
dotnet test tests/OpenSandbox.Web.Tests
cd frontend && npm test && npm run lint
```

### Screenshots

![Screenshot 1](./screenshots/1.jpg)

![Screenshot 2](./screenshots/2.jpg)

![Screenshot 3](./screenshots/3.jpg)

### License

MIT License

---

<a name="中文"></a>
## 中文

### 项目简介

OpenSandbox Admin 是 **OpenSandbox** 的现代化 Web 管理控制台——一个轻量级、安全的沙箱环境编排平台。该管理面板提供完整的界面来管理容器化沙箱实例、监控资源使用情况以及控制沙箱生命周期。

仓库采用前后端分离结构：React 单页应用 + .NET 10 API（封装官方 [Alibaba.OpenSandbox](https://www.nuget.org/packages/Alibaba.OpenSandbox) SDK）。

### 功能特性

- **仪表盘概览** - 实时统计沙箱状态（运行中、已暂停、创建中、错误）
- **沙箱管理** - 从容器镜像创建单个或批量沙箱；暂停、恢复、删除实例；按状态或元数据过滤和搜索
- **实时监控** - CPU 与内存指标经后端代理以 SSE 推送，浏览器无需直连沙箱端点
- **TTL 管理** - 通过时间选择器延长沙箱过期时间
- **批量操作** - JSON 导入或手动输入批量创建，带进度显示
- **单进程部署** - 后端托管前端构建产物并提供 SPA fallback
- **Keycloak 单点登录** - 控制台通过 Keycloak 登录，沙箱与卷各自区分 admin/viewer 角色

### 技术栈

| 领域 | 技术 |
|------|------|
| 前端 | React 19、TypeScript、Vite、Ant Design 5、TanStack Query、ECharts、axios、dayjs |
| 后端 | .NET 10、ASP.NET Core Minimal API、Alibaba.OpenSandbox 1.1.1 |
| 测试 | xUnit + WebApplicationFactory（后端）、Vitest + Testing Library（前端） |

### 仓库结构

```
├── backend/       # .NET 10 API（Contracts / Host / Infrastructure 分层）
├── frontend/      # React 19 + Vite + Ant Design 单页应用
├── tests/         # xUnit 测试项目
├── Dockerfile     # 多阶段构建：node 构建 -> dotnet 发布 -> 运行镜像
└── docker-compose.yml
```

### 快速开始

环境要求：.NET SDK 10.0.401+、Node.js 20.19+ 或 22.12+

```bash
# 终端 1 - 后端（http://localhost:5080）
cd backend
dotnet run

# 终端 2 - 前端开发服务器（http://localhost:3000，/api 代理到 5080）
cd frontend
npm install
cp .env.example .env.local   # Vite 开发服务器使用的 Keycloak realm/client
npm run dev
```

### 配置

后端配置位于 `backend/appsettings.json`：

```json
{
  "OpenSandbox": {
    "Domain": "https://sandbox.example.com",
    "ApiKey": "your-api-key"
  }
}
```

在 Docker 下运行时，compose 通过环境变量 `OpenSandbox__Domain` / `OpenSandbox__ApiKey` 覆盖同名配置（见 `.env.example`）。

控制台登录使用 `Auth` 配置，且为必填：

```json
{
  "Auth": {
    "Authority": "https://keycloak.example.com/realms/your-realm",
    "ClientId": "opensandbox-web",
    "RequireHttpsMetadata": true
  }
}
```

缺少 `Auth:Authority` 或 `Auth:ClientId` 时后端拒绝启动。Keycloak 客户端为公共客户端，启用 Standard Flow + PKCE S256，并需把控制台源（开发环境如 `http://localhost:3000/*`，Docker 环境如 `http://<控制台主机>:5080/*`）登记为 redirect URI 与 web origin，同时在 **Valid post logout redirect URIs** 中登记同一控制台源（如 `http://localhost:3000/*` 与 `http://<控制台主机>:5080/*`）。控制台注销时总会发送 `post_logout_redirect_uri`，未登记时注销后会落到 Keycloak 的错误页。

### API

所有端点以 `/api/v1` 为前缀：

| 端点 | 方法 | 说明 |
|------|------|------|
| `/health` | GET | 存活检查 |
| `/console/me` | GET | 当前控制台身份与角色 |
| `/sandboxes` | GET | 沙箱列表（分页、状态/元数据过滤） |
| `/sandboxes` | POST | 创建沙箱 |
| `/sandboxes/{id}` | GET | 沙箱详情 |
| `/sandboxes/{id}` | DELETE | 删除沙箱 |
| `/sandboxes/{id}/pause` | POST | 暂停沙箱 |
| `/sandboxes/{id}/resume` | POST | 恢复沙箱 |
| `/sandboxes/{id}/renew-expiration` | POST | 延长过期时间 |
| `/sandboxes/{id}/endpoints/{port}` | GET | 获取沙箱端口端点 |
| `/sandboxes/{id}/metrics` | GET | 指标快照 |
| `/sandboxes/{id}/metrics/watch` | GET | SSE 实时指标 |

错误响应统一为：`{ "code": "...", "message": "..." }`。

### Docker

```bash
# OpenSandbox 相关配置默认即可运行：compose 会同时启动 web（localhost:5080）与内置 opensandbox-server（127.0.0.1:8090）。
# AUTH_AUTHORITY 为必填：只有示例 .env 提供了项目共享的开发 realm。
cp .env.example .env
docker compose up -d
```

- `opensandbox-web` 通过 compose 网络访问 `opensandbox-server`，OpenSandbox 相关默认无需修改 `.env`；仅当连接外部 OpenSandbox 平台时才设置 `OPENSANDBOX_DOMAIN` / `OPENSANDBOX_API_KEY`。
- `AUTH_AUTHORITY` 为必填（缺少时 `docker compose` 立即失败）。示例 `.env` 指向项目共享的开发 realm `https://dev-passport.ai4c.cn/realms/ai4cbrain`，仅用于开发；生产环境必须使用自己的 realm，并把控制台源同时登记为 redirect URI 与 post logout redirect URI。
- `opensandbox-server` 挂载宿主机 `/var/run/docker.sock` 以创建沙箱容器（上游设计的固有行为），因此它能看到并清理宿主机上带 OpenSandbox 标签的过期沙箱容器；8090 仅绑定 127.0.0.1。
- 首次创建沙箱前建议预拉运行时镜像 `opensandbox/execd:v1.1.0` 与 `opensandbox/egress:v1.1.7`：冷拉可能超过后端默认 30s 请求超时（可在 .env 中调大 OPENSANDBOX_REQUEST_TIMEOUT_SECONDS）。
- 修改 `docker-compose.yml` 内联 server 配置后，需 `docker compose up -d --force-recreate opensandbox-server` 才会生效。
- 控制台现在要求 Keycloak 登录。对外开放 5080 前，先在 `opensandbox-web` 客户端登记控制台 redirect URI、web origin 与 post logout redirect URI；即使绕过界面，API 也会拒绝匿名请求。

### 测试

```bash
dotnet test tests/OpenSandbox.Web.Tests
cd frontend && npm test && npm run lint
```

### 截图

![截图 1](./screenshots/1.jpg)

![截图 2](./screenshots/2.jpg)

![截图 3](./screenshots/3.jpg)

### 许可证

MIT License
