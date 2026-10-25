import { afterEach, describe, expect, it, vi } from 'vitest'
import { apiClient } from '../client'
import { sandboxApi } from '../sandbox'
import type { CreateSandboxRequest } from '../types'

const request: CreateSandboxRequest = {
  image: { uri: 'alpine:latest' },
  entrypoint: ['sleep', '3600'],
  resourceLimits: { cpu: '500m', memory: '512Mi' },
}

describe('API timeouts', () => {
  afterEach(() => vi.restoreAllMocks())

  it('defaults to 30s to match the backend SDK timeout', () => {
    expect(apiClient.defaults.timeout).toBe(30000)
  })

  it('allows 120s for sandbox creation image pulls', async () => {
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ data: { id: 'sb-1' } })
    await sandboxApi.create(request)
    expect(post).toHaveBeenCalledWith('/sandboxes', request, { timeout: 120000 })
  })
})
