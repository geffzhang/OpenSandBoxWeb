import {
  buildAuthUrl,
  buildLogoutUrl,
  needsRefresh,
  parseCallback,
  randomState,
  resolveAuthConfig,
  tokenEndpoint,
  type AuthConfig,
} from './authModel'
import { createPkce } from './pkce'

export const STORAGE_KEYS = {
  access: 'osb_at',
  refresh: 'osb_rt',
  idToken: 'osb_it',
  expiresAt: 'osb_exp',
} as const

const TRANSACTION_PREFIX = 'osb_login_'
const TRANSACTION_TTL_MS = 15 * 60 * 1000
const CALLBACK_PARAMS = ['code', 'state', 'error', 'error_description', 'session_state', 'iss']

type LoginTransaction = {
  returnTo: string
  createdAt: number
  exchangeStarted: boolean
  codeVerifier: string
}

type TokenResponse = {
  access_token?: string
  expires_in?: number
  refresh_token?: string
  id_token?: string
}

let navigate: (url: string) => void = (url) => window.location.assign(url)

export function setAuthNavigatorForTests(fn: (url: string) => void): void {
  navigate = fn
}

export function resetAuthNavigatorForTests(): void {
  navigate = (url) => window.location.assign(url)
}

export function clearSession(): void {
  for (const key of Object.values(STORAGE_KEYS)) sessionStorage.removeItem(key)
}

export function currentAccessToken(now = Date.now()): string | null {
  const token = sessionStorage.getItem(STORAGE_KEYS.access)
  const expiresAt = Number(sessionStorage.getItem(STORAGE_KEYS.expiresAt) ?? 0)
  if (!token || !expiresAt || now >= expiresAt) return null
  return token
}

export function login(options: { returnTo?: string } = {}): void {
  const config = resolveAuthConfig()
  const state = randomState()
  const pkce = createPkce()
  pruneTransactions()

  const transaction: LoginTransaction = {
    returnTo: options.returnTo ?? currentReturnTo(),
    createdAt: Date.now(),
    exchangeStarted: false,
    codeVerifier: pkce.verifier,
  }
  sessionStorage.setItem(TRANSACTION_PREFIX + state, JSON.stringify(transaction))
  navigate(buildAuthUrl(config, redirectUri(), state, pkce.challenge))
}

export function logout(): void {
  const config = resolveAuthConfig()
  const idToken = sessionStorage.getItem(STORAGE_KEYS.idToken) ?? ''
  clearSession()
  navigate(buildLogoutUrl(config, redirectUri(), idToken))
}

export function requireLogin(): Promise<never> {
  clearSession()
  login()
  return new Promise<never>(() => {})
}

export async function getToken(): Promise<string> {
  const access = sessionStorage.getItem(STORAGE_KEYS.access)
  const expiresAt = Number(sessionStorage.getItem(STORAGE_KEYS.expiresAt) ?? 0)
  const now = Date.now()

  if (access && expiresAt > now && !needsRefresh(expiresAt, now)) return access

  if (sessionStorage.getItem(STORAGE_KEYS.refresh)) {
    try {
      await refreshSession()
      const refreshed = sessionStorage.getItem(STORAGE_KEYS.access)
      if (refreshed) return refreshed
    } catch {
      // 续签失败按未登录处理
    }
  }

  return requireLogin()
}

let refreshInFlight: Promise<void> | null = null

export function refreshSession(): Promise<void> {
  if (refreshInFlight) return refreshInFlight

  const refreshToken = sessionStorage.getItem(STORAGE_KEYS.refresh)
  if (!refreshToken) return Promise.reject(new Error('当前登录缺少续签凭据'))

  const request = exchange(resolveAuthConfig(), {
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
  })
  refreshInFlight = request
  void request
    .finally(() => {
      if (refreshInFlight === request) refreshInFlight = null
    })
    .catch(() => {})

  return request
}

export async function ensureAuthenticated(): Promise<void> {
  const callback = parseCallback(window.location.search)
  if (callback) {
    await completeLoginCallback(callback)
    return
  }

  if (currentAccessToken()) return
  return requireLogin()
}

let bootPromise: Promise<void> | null = null

export function ensureAuthenticatedOnce(): Promise<void> {
  bootPromise ??= ensureAuthenticated()
  return bootPromise
}

export async function completeLoginCallback(callback: {
  code: string
  state: string
}): Promise<void> {
  const key = TRANSACTION_PREFIX + callback.state
  const raw = sessionStorage.getItem(key)
  if (!raw) throw new Error('登录回调对不上：当前标签页没有这次登录记录，请重新登录')

  let transaction: LoginTransaction
  try {
    transaction = JSON.parse(raw) as LoginTransaction
  } catch {
    sessionStorage.removeItem(key)
    throw new Error('登录回调对不上：请重新登录')
  }

  if (Date.now() - transaction.createdAt >= TRANSACTION_TTL_MS) {
    sessionStorage.removeItem(key)
    throw new Error('登录超过 15 分钟没有完成，请重新登录')
  }
  if (transaction.exchangeStarted) {
    throw new Error('这次登录已经处理过，请重新登录')
  }
  if (!/^[A-Za-z0-9_-]{43}$/.test(transaction.codeVerifier)) {
    sessionStorage.removeItem(key)
    throw new Error('登录回调对不上：请重新登录')
  }

  sessionStorage.setItem(key, JSON.stringify({ ...transaction, exchangeStarted: true }))
  await exchange(resolveAuthConfig(), {
    grant_type: 'authorization_code',
    code: callback.code,
    redirect_uri: redirectUri(),
    code_verifier: transaction.codeVerifier,
  })
  sessionStorage.removeItem(key)
  window.history.replaceState(null, '', transaction.returnTo || '/')
}

async function exchange(config: AuthConfig, params: Record<string, string>): Promise<void> {
  const response = await fetch(tokenEndpoint(config), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: config.clientId, ...params }),
  })
  if (!response.ok) throw new Error(`登录令牌请求失败（HTTP ${response.status}）`)

  const body = (await response.json()) as TokenResponse
  if (!body.access_token) throw new Error('登录响应缺少 access_token')

  sessionStorage.setItem(STORAGE_KEYS.access, body.access_token)
  sessionStorage.setItem(
    STORAGE_KEYS.expiresAt,
    String(Date.now() + (body.expires_in ?? 300) * 1000),
  )
  if (body.refresh_token) sessionStorage.setItem(STORAGE_KEYS.refresh, body.refresh_token)
  if (body.id_token) sessionStorage.setItem(STORAGE_KEYS.idToken, body.id_token)
}

function currentReturnTo(): string {
  const url = new URL(window.location.href)
  for (const key of CALLBACK_PARAMS) url.searchParams.delete(key)
  const search = url.searchParams.toString()
  return `${url.pathname}${search ? `?${search}` : ''}`
}

function redirectUri(): string {
  return `${window.location.origin}/`
}

function pruneTransactions(): void {
  const now = Date.now()
  for (let index = sessionStorage.length - 1; index >= 0; index -= 1) {
    const key = sessionStorage.key(index)
    if (!key?.startsWith(TRANSACTION_PREFIX)) continue

    try {
      const transaction = JSON.parse(sessionStorage.getItem(key) ?? '') as LoginTransaction
      if (now - transaction.createdAt >= TRANSACTION_TTL_MS) sessionStorage.removeItem(key)
    } catch {
      sessionStorage.removeItem(key)
    }
  }
}
