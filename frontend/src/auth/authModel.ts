import { base64Url } from './pkce'

export type AuthConfig = { authority: string; clientId: string }

type InjectedAuth = { authority?: string; clientId?: string }
type RuntimeEnv = {
  DEV?: boolean
  VITE_AUTH_AUTHORITY?: string
  VITE_AUTH_CLIENT_ID?: string
}

const MISSING_CONFIG_MESSAGE =
  '登录域未配置：后端需在 index.html 注入 __OPENSANDBOX_AUTH__'

export function readInjectedAuth(): InjectedAuth | undefined {
  return (globalThis as { __OPENSANDBOX_AUTH__?: InjectedAuth }).__OPENSANDBOX_AUTH__
}

export function resolveAuthConfig(env: RuntimeEnv = import.meta.env): AuthConfig {
  const injected = readInjectedAuth()
  const buildTime = env.DEV
    ? {
        authority: env.VITE_AUTH_AUTHORITY,
        clientId: env.VITE_AUTH_CLIENT_ID,
      }
    : { authority: undefined, clientId: undefined }

  const authority = (injected?.authority?.trim() || buildTime.authority?.trim() || '')
    .replace(/\/+$/, '')
  const clientId = injected?.clientId?.trim() || buildTime.clientId?.trim() || ''

  if (!authority || !clientId) throw new Error(MISSING_CONFIG_MESSAGE)
  return { authority, clientId }
}

export function oidcBase(config: AuthConfig): string {
  return `${config.authority}/protocol/openid-connect`
}

export function tokenEndpoint(config: AuthConfig): string {
  return `${oidcBase(config)}/token`
}

export function buildAuthUrl(
  config: AuthConfig,
  redirectUri: string,
  state: string,
  codeChallenge: string,
): string {
  const query = new URLSearchParams({
    client_id: config.clientId,
    redirect_uri: redirectUri,
    response_type: 'code',
    scope: 'openid profile',
    state,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
  })
  return `${oidcBase(config)}/auth?${query.toString()}`
}

export function buildLogoutUrl(
  config: AuthConfig,
  redirectUri: string,
  idToken: string,
): string {
  const query = new URLSearchParams({
    client_id: config.clientId,
    post_logout_redirect_uri: redirectUri,
  })
  if (idToken) query.set('id_token_hint', idToken)
  return `${oidcBase(config)}/logout?${query.toString()}`
}

export class AuthorizationRejected extends Error {
  readonly code: string
  readonly description?: string

  constructor(code: string, description?: string) {
    super(description ? `登录被拒绝：${code}（${description}）` : `登录被拒绝：${code}`)
    this.name = 'AuthorizationRejected'
    this.code = code
    this.description = description
  }
}

export function parseCallback(search: string): { code: string; state: string } | null {
  const params = new URLSearchParams(search)
  const error = params.get('error')
  if (error) {
    throw new AuthorizationRejected(error, params.get('error_description') ?? undefined)
  }

  const code = params.get('code')
  const state = params.get('state')
  if (!code && !state) return null
  if (!code) throw new Error('登录回调缺少 code')
  if (!state) throw new Error('登录回调缺少 state')
  return { code, state }
}

export function needsRefresh(expiresAtMs: number, nowMs: number): boolean {
  return expiresAtMs - nowMs < 60_000
}

export function randomState(): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(16)))
}
