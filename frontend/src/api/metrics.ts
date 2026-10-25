import { apiClient } from './client'
import type { Metrics } from './types'

export const metricsApi = {
  async getSnapshot(id: string): Promise<Metrics> {
    const response = await apiClient.get<Metrics>(`/sandboxes/${id}/metrics`)
    return response.data
  },
}
