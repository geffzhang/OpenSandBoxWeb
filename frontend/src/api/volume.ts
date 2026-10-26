import { apiClient } from './client'
import type {
  CreateTenantKeyRequest,
  CreateTenantKeyResponse,
  CreateTenantRequest,
  TenantKeyListResponse,
  TenantListResponse,
  UpdateTenantRequest,
  VolumeDetail,
  VolumeImportRequest,
  VolumeImportResponse,
  VolumeLifecycleRequest,
  VolumeLifecycleResponse,
  VolumeListResponse,
  VolumeReport,
  VolumeScanResponse,
  VolumeTenant,
} from './types'

export interface VolumeListParams {
  page?: number
  pageSize?: number
  namespace?: string
  tenant?: string
  classification?: string
  lifecycle?: string
  mounted?: boolean
  q?: string
  sort?: string
  order?: 'asc' | 'desc'
}

const segment = (value: string) => encodeURIComponent(value)

export const volumeApi = {
  list: (params: VolumeListParams) =>
    apiClient.get<VolumeListResponse>('/volumes', { params }).then((response) => response.data),

  get: (namespace: string, claimName: string) =>
    apiClient
      .get<VolumeDetail>(`/volumes/${segment(namespace)}/${segment(claimName)}`)
      .then((response) => response.data),

  changeLifecycle: (
    namespace: string,
    claimName: string,
    request: VolumeLifecycleRequest,
  ) =>
    apiClient
      .post<VolumeLifecycleResponse>(
        `/volumes/${segment(namespace)}/${segment(claimName)}/lifecycle`,
        request,
      )
      .then((response) => response.data),

  scan: () =>
    apiClient
      .post<VolumeScanResponse>('/volumes/scan')
      .then((response) => response.data),

  report: () =>
    apiClient.get<VolumeReport>('/volumes/report').then((response) => response.data),

  import: (request: VolumeImportRequest) =>
    apiClient
      .post<VolumeImportResponse>('/volumes/import', request, { timeout: 120000 })
      .then((response) => response.data),

  tenants: () =>
    apiClient
      .get<TenantListResponse>('/volume-tenants')
      .then((response) => response.data.items),

  createTenant: (request: CreateTenantRequest) =>
    apiClient
      .post<VolumeTenant>('/volume-tenants', request)
      .then((response) => response.data),

  updateTenant: (tenantId: string, request: UpdateTenantRequest) =>
    apiClient
      .patch<VolumeTenant>(`/volume-tenants/${segment(tenantId)}`, request)
      .then((response) => response.data),

  keys: (tenantId: string) =>
    apiClient
      .get<TenantKeyListResponse>(`/volume-tenants/${segment(tenantId)}/keys`)
      .then((response) => response.data.items),

  createKey: (tenantId: string, request: CreateTenantKeyRequest) =>
    apiClient
      .post<CreateTenantKeyResponse>(
        `/volume-tenants/${segment(tenantId)}/keys`,
        request,
      )
      .then((response) => response.data),

  revokeKey: (tenantId: string, keyId: string) =>
    apiClient
      .post(`/volume-tenants/${segment(tenantId)}/keys/${segment(keyId)}/revoke`)
      .then(() => undefined),
}
