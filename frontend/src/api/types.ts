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

export interface VolumeListItem {
  id?: string | null
  namespace: string
  claimName: string
  tenant?: string | null
  tenantName?: string | null
  ownerKind?: string | null
  ownerId?: string | null
  role?: string | null
  mountPath?: string | null
  lifecycle?: string | null
  state?: string | null
  stateReason?: string | null
  classification: string
  labelState?: string | null
  violations: string[]
  registeredUid?: string | null
  observedUid?: string | null
  registeredAt?: string | null
  lastSeenAt?: string | null
  mounted?: boolean | null
  storageClass?: string | null
  capacity?: string | null
  createdAt?: string | null
}

export interface VolumeListResponse {
  items: VolumeListItem[]
  pagination: PaginationInfo
}

export interface ClusterVolume {
  namespace: string
  claimName: string
  uid: string
  phase: string
  storageClass?: string | null
  requestedStorage?: string | null
  capacity?: string | null
  createdAt: string
  deletionTimestamp?: string | null
  mounted: boolean
  mountedBy: string[]
  labels: Record<string, string>
  lifecycleLabel?: string | null
  tenantLabel?: string | null
  ownerLabel?: string | null
  contractVersionLabel?: string | null
  firstSeenAt: string
  lastSeenAt: string
}

export interface VolumeEvent {
  id: string
  volumeId?: string | null
  namespace: string
  claimName: string
  tenantId?: string | null
  type: string
  actor: string
  reason?: string | null
  dataJson: string
  createdAt: string
}

export interface VolumeDetail {
  volume: VolumeListItem
  observed?: ClusterVolume | null
  events: VolumeEvent[]
}

export interface VolumeLifecycleRequest {
  lifecycle: 'business' | 'ephemeral'
  reason?: string
}

export interface VolumeLifecycleResponse {
  status: string
  labelState: string
}

export interface VolumeReportCounts {
  observed: number
  registered: number
  unlabeled: number
  unregistered: number
  drift: number
  missing: number
  reappeared: number
  unmanaged: number
}

export interface VolumeCoverage {
  observed: number
  labeled: number
  registered: number
  labeledPercent: number
  registeredPercent: number
}

export interface VolumeDistributionItem {
  key: string
  count: number
}

export interface VolumeViolation {
  type: string
  namespace: string
  claimName: string
  message: string
}

export interface ScanError {
  namespace: string
  message: string
}

export interface ScanRun {
  id: string
  trigger: string
  startedAt: string
  finishedAt?: string | null
  namespacesScanned: number
  namespacesSucceeded: number
  volumeCount: number
  errors: ScanError[]
}

export interface VolumeScanResponse {
  scanId: string
  trigger: string
  startedAt: string
  finishedAt: string
  namespacesScanned: number
  namespacesSucceeded: number
  volumeCount: number
  errors: ScanError[]
}

export interface VolumeReport {
  lastScan?: ScanRun | null
  counts: VolumeReportCounts
  coverage: VolumeCoverage
  byTenant: VolumeDistributionItem[]
  byNamespace: VolumeDistributionItem[]
  violations: VolumeViolation[]
  truncated: boolean
}

export interface VolumeImportItem {
  tenant: string
  namespace: string
  claimName: string
  ownerKind: string
  ownerId: string
  role?: string | null
  mountPath?: string | null
  lifecycle: string
  sandboxId?: string | null
}

export interface VolumeImportRequest {
  dryRun?: boolean
  items: VolumeImportItem[]
}

export interface VolumeImportResult {
  namespace: string
  claimName: string
  status: string
  message?: string | null
}

export interface VolumeImportResponse {
  dryRun: boolean
  results: VolumeImportResult[]
}

export interface VolumeTenant {
  id: string
  displayName: string
  status: string
  namespaces: string[]
  volumeCount: number
  protectedCount: number
  ephemeralCount: number
  missingCount: number
  keyCount: number
  createdAt: string
}

export interface TenantListResponse {
  items: VolumeTenant[]
}

export interface CreateTenantRequest {
  id: string
  displayName: string
  namespaces: string[]
}

export interface UpdateTenantRequest {
  displayName?: string | null
  status?: string | null
  namespaces?: string[] | null
}

export interface TenantKey {
  id: string
  name: string
  prefix: string
  createdAt: string
  revokedAt?: string | null
  lastUsedAt?: string | null
}

export interface TenantKeyListResponse {
  items: TenantKey[]
}

export interface CreateTenantKeyRequest {
  name?: string | null
}

export interface CreateTenantKeyResponse {
  id: string
  key: string
  prefix: string
}
