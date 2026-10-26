import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeReport as VolumeReportData } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeReport from '../VolumeReport'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test', username: 'tester', roles: ['volume:admin'] },
    can: () => true,
  }),
}))

vi.mock('@/api/volume', () => ({
  volumeApi: { report: vi.fn(), scan: vi.fn() },
}))

const report: VolumeReportData = {
  lastScan: {
    id: 'scan-1',
    trigger: 'manual',
    startedAt: '2026-01-01T00:00:00Z',
    finishedAt: '2026-01-01T00:05:00Z',
    namespacesScanned: 2,
    namespacesSucceeded: 2,
    volumeCount: 4,
    errors: [],
  },
  counts: {
    observed: 42,
    registered: 30,
    unlabeled: 5,
    unregistered: 3,
    drift: 2,
    missing: 1,
    reappeared: 1,
    unmanaged: 0,
  },
  coverage: {
    observed: 42,
    labeled: 37,
    registered: 30,
    labeledPercent: 88.1,
    registeredPercent: 71.4,
  },
  byTenant: [{ key: 'goodcrew', count: 4 }],
  byNamespace: [{ key: 'opensandbox', count: 4 }],
  violations: [
    {
      type: 'unlabeled',
      namespace: 'opensandbox',
      claimName: 'pvc-1',
      message: '缺少 lifecycle 标签',
    },
  ],
  truncated: true,
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <VolumeReport />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeReport', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.report).mockResolvedValue(report)
  })

  it('renders classification counts, coverage, violations and the truncation alert', async () => {
    renderPage()

    expect(await screen.findByText('42')).toBeInTheDocument()
    expect(screen.getByText('观测卷')).toBeInTheDocument()
    expect(screen.getByText('30')).toBeInTheDocument()
    expect(screen.getByText('5')).toBeInTheDocument()
    expect(screen.getByText('3')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument()
    expect(screen.getByText('88.1%')).toBeInTheDocument()
    expect(screen.getByText('71.4%')).toBeInTheDocument()
    expect(screen.getByText('goodcrew')).toBeInTheDocument()
    expect(screen.getByText('pvc-1')).toBeInTheDocument()
    expect(screen.getByText('unlabeled')).toBeInTheDocument()
    expect(screen.getByText(/违规超过 200 条/)).toBeInTheDocument()
  })

  it('hides the truncation alert when the report is complete', async () => {
    vi.mocked(volumeApi.report).mockResolvedValue({ ...report, truncated: false })
    renderPage()

    expect(await screen.findByText('42')).toBeInTheDocument()
    expect(screen.queryByText(/违规超过 200 条/)).toBeNull()
    expect(screen.queryByText(/已截断/)).toBeNull()
  })
})
