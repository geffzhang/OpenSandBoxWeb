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
| Valid redirect URIs | `https://<控制台域名>/*`；开发环境追加 `http://localhost:3000/*` 与 `http://localhost:5080/*` |
| Valid post logout redirect URIs | `https://<控制台域名>/*`；开发环境追加 `http://localhost:3000/*` 与 `http://localhost:5080/*` |
| Web origins | 控制台源；开发环境追加 `http://localhost:3000` 与 `http://localhost:5080` |

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
2. `sandbox:viewer` 用户登录：落到 `/dashboard`；直接访问 `/volumes` 会被重定向回 `/`，最终仍落在 `/dashboard`（而不是显示无权限页）。
3. `volume:viewer` 用户登录：落到 `/volumes`；不能点「降级」「立即扫描」。
4. `volume:admin` 用户登录：可以变更 lifecycle、扫描、管理租户与 Key。
5. 匿名请求 `GET /api/v1/volumes` 返回 401；无 volume 角色的 JWT 返回 403。
6. 租户 API Key 调管理 API 返回 401；控制台 JWT 调 `/api/v1/tenant/volumes` 返回 401。
