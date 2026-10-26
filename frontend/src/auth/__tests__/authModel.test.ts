import { afterEach, describe, expect, it } from 'vitest'
import {
  AuthorizationRejected,
  buildAuthUrl,
  buildLogoutUrl,
  needsRefresh,
  parseCallback,
  randomState,
  resolveAuthConfig,
} from '../authModel'

const config = { authority: 'https://kc.example/realms/dev', clientId: 'opensandbox-web' }

type Injected = { authority?: string; clientId?: string }

afterEach(() => {
  delete (globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__
})

describe('resolveAuthConfig', () => {
  it('prefers the backend-injected config and trims trailing slashes', () => {
    ;(globalThis as { __OPENSANDBOX_AUTH__?: Injected }).__OPENSANDBOX_AUTH__ = {
      authority: 'https://kc.example/realms/dev/',
      clientId: 'opensandbox-web',
    }

    expect(resolveAuthConfig({ DEV: false })).toEqual(config)
  })

  it('does not fall back to build-time values in production', () => {
    expect(() =>
      resolveAuthConfig({
        DEV: false,
        VITE_AUTH_AUTHORITY: 'https://kc.example/realms/dev',
        VITE_AUTH_CLIENT_ID: 'opensandbox-web',
      }),
    ).toThrow('__OPENSANDBOX_AUTH__')
  })

  it('uses Vite env values in development', () => {
    expect(
      resolveAuthConfig({
        DEV: true,
        VITE_AUTH_AUTHORITY: 'https://kc.example/realms/dev',
        VITE_AUTH_CLIENT_ID: 'opensandbox-web',
      }),
    ).toEqual(config)
  })

  it('rejects blank values', () => {
    expect(() =>
      resolveAuthConfig({ DEV: true, VITE_AUTH_AUTHORITY: '  ', VITE_AUTH_CLIENT_ID: 'x' }),
    ).toThrow('登录域未配置')
  })
})

describe('URL builders', () => {
  it('builds an authorization-code URL with PKCE S256', () => {
    const url = new URL(
      buildAuthUrl(config, 'http://localhost:3000/', 'state-1', 'challenge-1'),
    )

    expect(`${url.origin}${url.pathname}`).toBe(
      'https://kc.example/realms/dev/protocol/openid-connect/auth',
    )
    expect(url.searchParams.get('client_id')).toBe('opensandbox-web')
    expect(url.searchParams.get('response_type')).toBe('code')
    expect(url.searchParams.get('redirect_uri')).toBe('http://localhost:3000/')
    expect(url.searchParams.get('scope')).toBe('openid profile')
    expect(url.searchParams.get('state')).toBe('state-1')
    expect(url.searchParams.get('code_challenge')).toBe('challenge-1')
    expect(url.searchParams.get('code_challenge_method')).toBe('S256')
  })

  it('builds a logout URL with the id token hint', () => {
    const url = new URL(buildLogoutUrl(config, 'http://localhost:3000/', 'id-1'))

    expect(`${url.origin}${url.pathname}`).toBe(
      'https://kc.example/realms/dev/protocol/openid-connect/logout',
    )
    expect(url.searchParams.get('client_id')).toBe('opensandbox-web')
    expect(url.searchParams.get('post_logout_redirect_uri')).toBe('http://localhost:3000/')
    expect(url.searchParams.get('id_token_hint')).toBe('id-1')
  })
})

describe('parseCallback', () => {
  it('returns code and state', () => {
    expect(parseCallback('?code=abc&state=s1')).toEqual({ code: 'abc', state: 's1' })
  })

  it('returns null when there is no callback', () => {
    expect(parseCallback('')).toBeNull()
    expect(parseCallback('?foo=bar')).toBeNull()
  })

  it('throws a typed error when Keycloak rejects the login', () => {
    expect(() => parseCallback('?error=access_denied&error_description=denied')).toThrow(
      AuthorizationRejected,
    )
  })

  it('requires both code and state', () => {
    expect(() => parseCallback('?code=abc')).toThrow('state')
    expect(() => parseCallback('?state=s1')).toThrow('code')
  })
})

describe('refresh threshold and state', () => {
  it('refreshes inside the 60 second window', () => {
    expect(needsRefresh(1_000_000, 1_000_000 - 59_999)).toBe(true)
    expect(needsRefresh(1_000_000, 1_000_000 - 60_000)).toBe(false)
  })

  it('creates a 22-character random state', () => {
    const first = randomState()
    const second = randomState()
    expect(first).toMatch(/^[A-Za-z0-9_-]{22}$/)
    expect(second).not.toBe(first)
  })
})
