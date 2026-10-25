export interface ImageAuth {
  username: string
  password: string
}

export interface ImageRef {
  uri: string
  auth?: ImageAuth
}

export interface SandboxStatus {
  state: string
  reason?: string
  message?: string
}

export interface Sandbox {
  id: string
  image: ImageRef
  status: SandboxStatus
  metadata?: Record<string, string>
  entrypoint: string[]
  expiresAt?: string
  createdAt: string
}

export interface NetworkRule {
  action: string
  target: string
}

export interface NetworkPolicy {
  defaultAction?: string
  egress?: NetworkRule[]
}

export interface VolumeSpec {
  name: string
  mountPath: string
  readOnly?: boolean
  subPath?: string
  hostPath?: string
  pvcClaimName?: string
}

export interface CreateSandboxRequest {
  image: ImageRef
  timeout?: number | null
  resourceLimits: Record<string, string>
  entrypoint: string[]
  env?: Record<string, string>
  metadata?: Record<string, string>
  networkPolicy?: NetworkPolicy
  volumes?: VolumeSpec[]
  extensions?: Record<string, string>
}

export interface CreateSandboxResponse {
  id: string
  status: SandboxStatus
  metadata?: Record<string, string>
  expiresAt?: string
  createdAt: string
  entrypoint: string[]
}

export interface ListSandboxesParams {
  state?: string[]
  metadata?: Record<string, string>
  page?: number
  pageSize?: number
}

export interface PaginationInfo {
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
  hasNextPage: boolean
}

export interface ListSandboxesResponse {
  items: Sandbox[]
  pagination: PaginationInfo
}

export interface RenewExpirationRequest {
  expiresAt: string
}

export interface RenewExpirationResponse {
  expiresAt?: string
}

export interface Endpoint {
  endpoint: string
  headers?: Record<string, string>
}

export interface Metrics {
  cpuCount: number
  cpuUsedPct: number
  memTotalMib: number
  memUsedMib: number
  timestamp: number
}

export interface MetricsPoint {
  timestamp: number
  cpu: number
  memory: number
}

export interface ErrorResponse {
  code: string
  message: string
}
