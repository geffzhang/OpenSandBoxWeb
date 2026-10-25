import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  STORAGE_KEYS,
  clearSession,
  ensureAuthenticated,
  getToken,
  login,
  resetAuthNavigatorForTests,
  setAuthNavigatorForTests,
} from '../auth'
import { challengeFor } from '../pkce'
import { AuthorizationRejected } from '../authModel'

type Injected = { authority?: string; clientId?: string }

let navigated = ''

beforeEach(() => {
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
  ;(globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__ = {
    authority: 'https://kc.example/realms/dev',
    clientId: 'opensandbox-web',
  }
  navigated = ''
  setAuthNavigatorForTests((url) => {
    navigated = url
  })
})

afterEach(() => {
  resetAuthNavigatorForTests()
  vi.unstubAllGlobals()
  delete (globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
})

describe('login', () => {
  it('stores a PKCE transaction and redirects to Keycloak', () => {
    login({ returnTo: '/sandboxes' })

    const url = new URL(navigated)
    expect(url.pathname).toBe('/realms/dev/protocol/openid-connect/auth')
    const state = url.searchParams.get('state')!
    expect(state).toMatch(/^[A-Za-z0-9_-]{22}$/)

    const stored = JSON.parse(sessionStorage.getItem(`osb_login_${state}`)!)
    expect(stored.returnTo).toBe('/sandboxes')
    expect(stored.exchangeStarted).toBe(false)
    expect(stored.codeVerifier).toMatch(/^[A-Za-z0-9_-]{43}$/)
    expect(url.searchParams.get('code_challenge')).toBe(challengeFor(stored.codeVerifier))
  })

  it('strips stale OIDC parameters from returnTo', () => {
    window.history.replaceState(null, '', '/sandboxes?code=old&state=old&tab=1')

    login()

    const state = new URL(navigated).searchParams.get('state')!
    const stored = JSON.parse(sessionStorage.getItem(`osb_login_${state}`)!)
    expect(stored.returnTo).toBe('/sandboxes?tab=1')
  })
})

describe('authorization code exchange', () => {
  it('exchanges the code, stores tokens, and restores returnTo', async () => {
    const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/sandboxes',
        createdAt: Date.now(),
        exchangeStarted: false,
        codeVerifier: verifier,
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        access_token: 'at',
        expires_in: 300,
        refresh_token: 'rt',
        id_token: 'it',
      }),
    })
    vi.stubGlobal('fetch', fetchMock)

    await ensureAuthenticated()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('https://kc.example/realms/dev/protocol/openid-connect/token')
    const body = (init as { body: URLSearchParams }).body
    expect(body.get('grant_type')).toBe('authorization_code')
    expect(body.get('code')).toBe('abc')
    expect(body.get('code_verifier')).toBe(verifier)
    expect(body.get('redirect_uri')).toBe(`${window.location.origin}/`)
    expect(sessionStorage.getItem(STORAGE_KEYS.access)).toBe('at')
    expect(sessionStorage.getItem(STORAGE_KEYS.refresh)).toBe('rt')
    expect(sessionStorage.getItem(STORAGE_KEYS.idToken)).toBe('it')
    expect(sessionStorage.getItem('osb_login_s1')).toBeNull()
    expect(window.location.pathname).toBe('/sandboxes')
  })

  it('rejects a replayed transaction', async () => {
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/',
        createdAt: Date.now(),
        exchangeStarted: true,
        codeVerifier: 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')

    await expect(ensureAuthenticated()).rejects.toThrow('已经处理过')
  })

  it('rejects an expired transaction', async () => {
    sessionStorage.setItem(
      'osb_login_s1',
      JSON.stringify({
        returnTo: '/',
        createdAt: Date.now() - 16 * 60 * 1000,
        exchangeStarted: false,
        codeVerifier: 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      }),
    )
    window.history.replaceState(null, '', '/?code=abc&state=s1')

    await expect(ensureAuthenticated()).rejects.toThrow('15 分钟')
  })

  it('rejects an unknown state', async () => {
    window.history.replaceState(null, '', '/?code=abc&state=missing')

    await expect(ensureAuthenticated()).rejects.toThrow('没有这次登录记录')
  })

  it('surfaces Keycloak callback errors', async () => {
    window.history.replaceState(null, '', '/?error=access_denied&error_description=denied')

    await expect(ensureAuthenticated()).rejects.toBeInstanceOf(AuthorizationRejected)
  })
})

describe('token refresh', () => {
  it('uses a single in-flight refresh for concurrent callers', async () => {
    let resolveResponse!: (value: unknown) => void
    const fetchMock = vi.fn(
      (_url: string, _init: { body: URLSearchParams }) =>
        new Promise((resolve) => {
          resolveResponse = resolve
        }),
    )
    vi.stubGlobal('fetch', fetchMock)
    sessionStorage.setItem(STORAGE_KEYS.refresh, 'rt')
    sessionStorage.setItem(STORAGE_KEYS.expiresAt, String(Date.now() - 1))

    const first = getToken()
    const second = getToken()

    expect(fetchMock).toHaveBeenCalledTimes(1)

    resolveResponse({
      ok: true,
      json: async () => ({ access_token: 'at2', expires_in: 300 }),
    })

    await expect(Promise.all([first, second])).resolves.toEqual(['at2', 'at2'])
    const body = (fetchMock.mock.calls[0][1] as { body: URLSearchParams }).body
    expect(body.get('grant_type')).toBe('refresh_token')
    expect(body.get('refresh_token')).toBe('rt')
  })

  it('starts a new login when there is no refresh token', async () => {
    const token = getToken()

    expect(navigated).toContain('/protocol/openid-connect/auth?')
    expect(await Promise.race([token, Promise.resolve('pending')])).toBe('pending')
    expect(clearSession).toBeTypeOf('function')
  })
})
