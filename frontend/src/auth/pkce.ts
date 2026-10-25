import { sha256 } from '@noble/hashes/sha2.js'

export function base64Url(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function createVerifier(randomBytes?: Uint8Array): string {
  return base64Url(randomBytes ?? crypto.getRandomValues(new Uint8Array(32)))
}

export function challengeFor(verifier: string): string {
  return base64Url(sha256(new TextEncoder().encode(verifier)))
}

export function createPkce(): { verifier: string; challenge: string } {
  const verifier = createVerifier()
  return { verifier, challenge: challengeFor(verifier) }
}
