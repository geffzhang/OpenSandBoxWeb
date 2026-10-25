import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { CreateSandboxRequest, CreateSandboxResponse } from '@/api/types'

export interface BatchCreateOptions {
  concurrency?: number
  onProgress?: (completed: number, total: number) => void
  createFn?: (item: CreateSandboxRequest) => Promise<CreateSandboxResponse>
}

export interface BatchResult {
  successful: { request: CreateSandboxRequest; id: string }[]
  failed: { request: CreateSandboxRequest; error: string }[]
}

export async function batchCreateSandboxes(
  items: CreateSandboxRequest[],
  options?: BatchCreateOptions,
): Promise<BatchResult> {
  const concurrency = options?.concurrency ?? 3
  const createFn = options?.createFn ?? sandboxApi.create
  const successful: BatchResult['successful'] = []
  const failed: BatchResult['failed'] = []
  let completed = 0

  for (let i = 0; i < items.length; i += concurrency) {
    const batch = items.slice(i, i + concurrency)
    const results = await Promise.allSettled(batch.map((item) => createFn(item)))
    results.forEach((result, index) => {
      if (result.status === 'fulfilled') {
        successful.push({ request: batch[index], id: result.value.id })
      } else {
        failed.push({ request: batch[index], error: extractErrorMessage(result.reason) })
      }
      completed += 1
      options?.onProgress?.(completed, items.length)
    })
  }

  return { successful, failed }
}
