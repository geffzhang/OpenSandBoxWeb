import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import Dashboard from '../Dashboard'

const authState = vi.hoisted(() => ({ can: () => true as boolean }))

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test', username: 'tester', roles: [] },
    can: authState.can,
  }),
}))

vi.mock('@/hooks/usePolling', () => ({
  usePolling: () => ({ data: { items: [] } }),
}))

function renderDashboard() {
  const router = createMemoryRouter([{ path: '/', element: <Dashboard /> }], {
    initialEntries: ['/'],
  })
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('Dashboard', () => {
  it('shows the write quick actions to sandbox admins', async () => {
    authState.can = () => true
    renderDashboard()

    expect(await screen.findByRole('button', { name: /创建沙箱/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /批量创建/ })).toBeInTheDocument()
  })

  it('hides the write quick actions from sandbox viewers', async () => {
    authState.can = () => false
    renderDashboard()

    // 统计卡片先渲染，确认整个页面已挂载。
    expect(await screen.findByText('Running')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /创建沙箱/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /批量创建/ })).toBeNull()
  })
})
