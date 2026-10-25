import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { apiClient } from '../client'
import { requireLogin } from '@/auth/auth'

vi.mock('@/auth/auth', () => ({
  getToken: vi.fn().mockResolvedValue('access-token'),
  requireLogin: vi.fn().mockResolvedValue(undefined),
}))

// 自定义 adapter 不会自动调用 axios 的 settle：这里按真实适配器的行为抛出带响应的 AxiosError。
const rejectWith =
  (status: number) =>
  async (config: InternalAxiosRequestConfig): Promise<never> => {
    const response = { status, statusText: 'Test', headers: {}, data: {}, config }
    throw new AxiosError(
      `Request failed with status code ${status}`,
      AxiosError.ERR_BAD_REQUEST,
      config,
      undefined,
      response,
    )
  }

describe('401 interceptor', () => {
  beforeEach(() => {
    vi.mocked(requireLogin).mockClear()
  })

  it('starts a new login when a request returns 401', async () => {
    const request = apiClient.get('/x', { adapter: rejectWith(401) })

    await expect(request).rejects.toBeInstanceOf(AxiosError)
    expect(requireLogin).toHaveBeenCalledTimes(1)
  })

  it('does not start a login for a 403', async () => {
    const request = apiClient.get('/x', { adapter: rejectWith(403) })

    await expect(request).rejects.toBeInstanceOf(AxiosError)
    expect(requireLogin).not.toHaveBeenCalled()
  })
})
