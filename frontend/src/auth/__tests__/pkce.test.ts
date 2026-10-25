import { describe, expect, it } from 'vitest'
import { base64Url, challengeFor, createPkce, createVerifier } from '../pkce'

describe('pkce', () => {
  it('matches the RFC 7636 Appendix B vector', () => {
    expect(challengeFor('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe(
      'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
    )
  })

  it('creates a 43-character base64url verifier', () => {
    expect(createVerifier()).toMatch(/^[A-Za-z0-9_-]{43}$/)
  })

  it('returns a matching challenge pair', () => {
    const { verifier, challenge } = createPkce()
    expect(challenge).toBe(challengeFor(verifier))
    expect(challenge).toMatch(/^[A-Za-z0-9_-]{43}$/)
  })

  it('encodes bytes without padding or unsafe characters', () => {
    expect(base64Url(new Uint8Array([251, 255]))).toBe('-_8')
  })
})
