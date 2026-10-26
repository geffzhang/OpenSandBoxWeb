import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeTenants from '../VolumeTenants'

const authState = vi.hoisted(() => ({ canManage: true }))

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test', username: 'tester', roles: [] },
    can: (role: string) => role === 'volume:admin' && authState.canManage,
  }),
}))

vi.mock('@/api/volume', () => ({
  volumeApi: {
    tenants: vi.fn(),
    createTenant: vi.fn(),
    updateTenant: vi.fn(),
    keys: vi.fn(),
    createKey: vi.fn(),
    revokeKey: vi.fn(),
  },
}))

const tenants: VolumeTenant[] = [
  {
    id: 'goodcrew',
    displayName: 'GoodCrew',
    status: 'active',
    namespaces: ['opensandbox'],
    volumeCount: 3,
    protectedCount: 2,
    ephemeralCount: 1,
    missingCount: 0,
    keyCount: 1,
    createdAt: '2026-01-01T00:00:00Z',
  },
]

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <VolumeTenants />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeTenants', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.tenants).mockResolvedValue(tenants)
  })

  it('shows tenant rows and admin actions to volume admins', async () => {
    authState.canManage = true
    renderPage()

    expect(await screen.findByText('GoodCrew')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /新建租户/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /编\s*辑/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Key/ })).toBeInTheDocument()
  })

  it('hides tenant create and edit actions from viewers', async () => {
    authState.canManage = false
    renderPage()

    expect(await screen.findByText('GoodCrew')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /新建租户/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /编\s*辑/ })).toBeNull()
    // Key 面板对查看者只读，入口仍保留。
    expect(screen.getByRole('button', { name: /Key/ })).toBeInTheDocument()
  })
})
