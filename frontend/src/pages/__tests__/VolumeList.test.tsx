import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeListResponse, VolumeReport } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeList from '../VolumeList'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { username: 'alice', roles: ['volume:viewer'] },
    can: (role: string) => role === 'volume:viewer',
  }),
}))

vi.mock('@/api/volume', () => ({
  volumeApi: {
    list: vi.fn(),
    report: vi.fn(),
    scan: vi.fn(),
    changeLifecycle: vi.fn(),
  },
}))

const listResponse: VolumeListResponse = {
  items: [
    {
      namespace: 'opensandbox',
      claimName: 'pvc-1',
      tenant: 'goodcrew',
      tenantName: 'GoodCrew',
      classification: 'registered',
      lifecycle: 'business',
      state: 'active',
      mounted: true,
      violations: [],
    },
  ],
  pagination: {
    page: 1,
    pageSize: 20,
    totalItems: 1,
    totalPages: 1,
    hasNextPage: false,
  },
}

const report: VolumeReport = {
  lastScan: null,
  counts: {
    observed: 1,
    registered: 1,
    unlabeled: 0,
    unregistered: 0,
    drift: 0,
    missing: 0,
    reappeared: 0,
    unmanaged: 0,
  },
  coverage: {
    observed: 1,
    labeled: 1,
    registered: 1,
    labeledPercent: 100,
    registeredPercent: 100,
  },
  byTenant: [],
  byNamespace: [],
  violations: [],
  truncated: false,
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={['/volumes']}>
            <Routes>
              <Route path="/volumes" element={<VolumeList />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeList', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.list).mockResolvedValue(listResponse)
    vi.mocked(volumeApi.report).mockResolvedValue(report)
  })

  it('renders server results and hides admin actions from viewers', async () => {
    renderPage()

    expect(await screen.findByText('pvc-1')).toBeInTheDocument()
    expect(screen.getByText('已登记')).toBeInTheDocument()
    expect(screen.getByText('GoodCrew')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /立即扫描/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /降级/ })).not.toBeInTheDocument()
  })

  it('passes the URL filters to the API', async () => {
    renderPage()

    await screen.findByText('pvc-1')
    expect(volumeApi.list).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 20,
        sort: 'namespace',
        order: 'asc',
      }),
    )
  })
})
