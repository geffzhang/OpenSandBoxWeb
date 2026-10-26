import { afterEach, describe, expect, it, vi } from 'vitest'
import { apiClient } from '../client'
import { volumeApi } from '../volume'

describe('volumeApi', () => {
  afterEach(() => vi.restoreAllMocks())

  it('encodes namespace and claim name in detail paths', async () => {
    const get = vi.spyOn(apiClient, 'get').mockResolvedValue({ data: { volume: {} } })

    await volumeApi.get('ns a', 'pvc/1')

    expect(get).toHaveBeenCalledWith('/volumes/ns%20a/pvc%2F1')
  })

  it('allows 120s for imports', async () => {
    const post = vi
      .spyOn(apiClient, 'post')
      .mockResolvedValue({ data: { dryRun: true, results: [] } })
    const request = { dryRun: true, items: [] }

    await volumeApi.import(request)

    expect(post).toHaveBeenCalledWith('/volumes/import', request, { timeout: 120000 })
  })

  it('revokes a tenant key with POST and no body', async () => {
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ data: null })

    await volumeApi.revokeKey('goodcrew', 'key-1')

    expect(post).toHaveBeenCalledWith('/volume-tenants/goodcrew/keys/key-1/revoke')
  })
})
