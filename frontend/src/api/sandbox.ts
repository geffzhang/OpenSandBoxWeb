import { apiClient } from './client'
import type {
  CreateSandboxRequest,
  CreateSandboxResponse,
  Endpoint,
  ListSandboxesParams,
  ListSandboxesResponse,
  RenewExpirationRequest,
  RenewExpirationResponse,
  Sandbox,
} from './types'

export const sandboxApi = {
  async create(data: CreateSandboxRequest): Promise<CreateSandboxResponse> {
    const response = await apiClient.post<CreateSandboxResponse>('/sandboxes', data)
    return response.data
  },

  async list(params?: ListSandboxesParams): Promise<ListSandboxesResponse> {
    const query = new URLSearchParams()
    params?.state?.forEach((s) => query.append('state', s))
    Object.entries(params?.metadata ?? {}).forEach(([key, value]) =>
      query.append('metadata', `${key}=${value}`),
    )
    if (params?.page) query.append('page', String(params.page))
    if (params?.pageSize) query.append('pageSize', String(params.pageSize))
    const response = await apiClient.get<ListSandboxesResponse>(
      `/sandboxes?${query.toString()}`,
    )
    return response.data
  },

  async get(id: string): Promise<Sandbox> {
    const response = await apiClient.get<Sandbox>(`/sandboxes/${id}`)
    return response.data
  },

  async remove(id: string): Promise<void> {
    await apiClient.delete(`/sandboxes/${id}`)
  },

  async pause(id: string): Promise<void> {
    await apiClient.post(`/sandboxes/${id}/pause`)
  },

  async resume(id: string): Promise<void> {
    await apiClient.post(`/sandboxes/${id}/resume`)
  },

  async renewExpiration(
    id: string,
    data: RenewExpirationRequest,
  ): Promise<RenewExpirationResponse> {
    const response = await apiClient.post<RenewExpirationResponse>(
      `/sandboxes/${id}/renew-expiration`,
      data,
    )
    return response.data
  },

  async getEndpoint(id: string, port: number): Promise<Endpoint> {
    const response = await apiClient.get<Endpoint>(`/sandboxes/${id}/endpoints/${port}`)
    return response.data
  },
}
