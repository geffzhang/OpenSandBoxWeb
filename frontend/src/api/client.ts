import axios from 'axios'

// 与后端 SDK 默认 RequestTimeoutSeconds 一致；创建请求单独放宽（见 sandbox.ts）。
export const apiClient = axios.create({
  baseURL: '/api/v1',
  timeout: 30000,
})

export function extractErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error) && error.response?.data) {
    const data = error.response.data as { message?: string }
    if (data.message) return data.message
  }
  return error instanceof Error ? error.message : 'Unknown error'
}
