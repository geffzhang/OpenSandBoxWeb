import { describe, expect, it, vi } from 'vitest'
import { batchCreateSandboxes } from '../batchCreate'
import type { CreateSandboxRequest, CreateSandboxResponse } from '@/api/types'

const item = (index: number): CreateSandboxRequest => ({
  image: { uri: 'ubuntu:24.04' },
  entrypoint: ['/bin/sh'],
  resourceLimits: { cpu: '500m' },
  metadata: { index: String(index) },
})

const created = (id: string): CreateSandboxResponse => ({
  id,
  status: { state: 'Pending' },
  createdAt: '2026-01-01T00:00:00Z',
  entrypoint: ['/bin/sh'],
})

describe('batchCreateSandboxes', () => {
  it('creates all items and reports progress in order', async () => {
    const onProgress = vi.fn()
    const createFn = vi.fn((req: CreateSandboxRequest) =>
      Promise.resolve(created(`sb-${req.metadata!.index}`)),
    )

    const result = await batchCreateSandboxes([item(0), item(1)], {
      concurrency: 1,
      onProgress,
      createFn,
    })

    expect(result.successful).toHaveLength(2)
    expect(result.failed).toHaveLength(0)
    expect(onProgress).toHaveBeenNthCalledWith(1, 1, 2)
    expect(onProgress).toHaveBeenNthCalledWith(2, 2, 2)
  })

  it('splits successful and failed items', async () => {
    const createFn = vi.fn((req: CreateSandboxRequest) =>
      req.metadata!.index === '1'
        ? Promise.reject(new Error('image pull failed'))
        : Promise.resolve(created(`sb-${req.metadata!.index}`)),
    )

    const result = await batchCreateSandboxes([item(0), item(1), item(2)], {
      concurrency: 3,
      createFn,
    })

    expect(result.successful).toHaveLength(2)
    expect(result.failed).toHaveLength(1)
    expect(result.failed[0].error).toBe('image pull failed')
  })

  it('limits in-flight requests to concurrency', async () => {
    let active = 0
    const createFn = vi.fn(async (req: CreateSandboxRequest) => {
      active += 1
      expect(active).toBeLessThanOrEqual(2)
      await new Promise((resolve) => setTimeout(resolve, 10))
      active -= 1
      return created(`sb-${req.metadata!.index}`)
    })

    await batchCreateSandboxes([item(0), item(1), item(2), item(3)], {
      concurrency: 2,
      createFn,
    })

    expect(createFn).toHaveBeenCalledTimes(4)
  })
})
