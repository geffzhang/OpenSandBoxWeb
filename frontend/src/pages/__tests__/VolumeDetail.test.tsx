import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeDetail as VolumeDetailData } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeDetail from '../VolumeDetail'

vi.mock('@/api/volume', () => ({
  volumeApi: { get: vi.fn() },
}))

const detail: VolumeDetailData = {
  volume: {
    namespace: 'opensandbox',
    claimName: 'pvc-1',
    tenant: 'goodcrew',
    tenantName: 'GoodCrew',
    ownerKind: 'employee',
    ownerId: '3f2a',
    role: 'meta',
    mountPath: '/workspace',
    lifecycle: 'business',
    state: 'active',
    classification: 'registered',
    labelState: 'applied',
    violations: [],
  },
  observed: {
    namespace: 'opensandbox',
    claimName: 'pvc-1',
    uid: 'uid-1',
    phase: 'Bound',
    storageClass: 'standard',
    requestedStorage: '1Gi',
    capacity: '2Gi',
    createdAt: '2026-01-01T00:00:00Z',
    deletionTimestamp: null,
    mounted: true,
    mountedBy: ['pod-1'],
    labels: { 'opensandbox.io/lifecycle': 'business' },
    firstSeenAt: '2026-01-01T00:00:00Z',
    lastSeenAt: '2026-01-02T00:00:00Z',
  },
  events: [
    {
      id: 'event-1',
      volumeId: null,
      namespace: 'opensandbox',
      claimName: 'pvc-1',
      tenantId: 'goodcrew',
      type: 'lifecycle-changed',
      actor: 'alice',
      reason: '业务确认',
      dataJson: '{}',
      createdAt: '2026-01-02T00:00:00Z',
    },
  ],
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={['/volumes/opensandbox/pvc-1']}>
            <Routes>
              <Route path="/volumes/:namespace/:claimName" element={<VolumeDetail />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeDetail', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.get).mockResolvedValue(detail)
  })

  it('renders ledger fields, cluster observation and the event timeline', async () => {
    renderPage()

    expect(await screen.findByText('opensandbox/pvc-1')).toBeInTheDocument()
    expect(screen.getByText('GoodCrew')).toBeInTheDocument()
    expect(screen.getByText('/workspace')).toBeInTheDocument()
    expect(screen.getByText('applied')).toBeInTheDocument()
    expect(screen.getByText('Bound')).toBeInTheDocument()
    expect(screen.getByText('standard')).toBeInTheDocument()
    expect(screen.getByText('pod-1')).toBeInTheDocument()
    expect(volumeApi.get).toHaveBeenCalledWith('opensandbox', 'pvc-1')
    expect(screen.getByText('lifecycle-changed')).toBeInTheDocument()
    expect(screen.getByText(/alice/)).toBeInTheDocument()
    expect(screen.getByText('业务确认')).toBeInTheDocument()
  })

  it('shows the not-found state when the ledger load fails', async () => {
    vi.mocked(volumeApi.get).mockRejectedValueOnce(new Error('not found'))
    renderPage()

    expect(await screen.findByText('卷不存在或无权访问')).toBeInTheDocument()
    expect(screen.getByText(/not found/)).toBeInTheDocument()
  })
})
