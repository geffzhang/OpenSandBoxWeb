import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import { useAuth } from '@/auth/AuthContext'
import AppLayout from '../AppLayout'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

const mockedUseAuth = vi.mocked(useAuth)

function renderLayout() {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [{ index: true, element: <div>页面内容</div> }],
      },
    ],
    { initialEntries: ['/'] },
  )

  return render(
    <ConfigProvider>
      <AntApp>
        <RouterProvider router={router} />
      </AntApp>
    </ConfigProvider>,
  )
}

function auth(roles: string[], can: (role: string) => boolean) {
  return {
    me: { subject: 'user-1', username: 'alice', roles },
    can,
  }
}

describe('AppLayout', () => {
  beforeEach(() => mockedUseAuth.mockReset())

  it('renders the volume menu group and role tags', () => {
    mockedUseAuth.mockReturnValue(auth(['sandbox:admin', 'volume:admin'], () => true))

    renderLayout()

    expect(screen.getByText('OpenSandbox Admin')).toBeInTheDocument()
    expect(screen.getByText('仪表盘')).toBeInTheDocument()
    expect(screen.getByText('沙箱列表')).toBeInTheDocument()
    expect(screen.getByText('批量创建')).toBeInTheDocument()
    expect(screen.getByText('设置')).toBeInTheDocument()
    expect(screen.getByText('卷管理')).toBeInTheDocument()
    expect(screen.getByText('卷列表')).toBeInTheDocument()
    expect(screen.getByText('租户管理')).toBeInTheDocument()
    expect(screen.getByText('违规报告')).toBeInTheDocument()
    expect(screen.getByText('回填导入')).toBeInTheDocument()
    expect(screen.getByText('volume:admin')).toBeInTheDocument()
    expect(screen.getByText('页面内容')).toBeInTheDocument()
  })

  it('shows volume entries and no sandbox admin entries for volume-only viewers', () => {
    mockedUseAuth.mockReturnValue(
      auth(['volume:viewer'], (role) => role === 'volume:viewer'),
    )

    renderLayout()

    expect(screen.getByText('卷管理')).toBeInTheDocument()
    expect(screen.getByText('卷列表')).toBeInTheDocument()
    expect(screen.getByText('设置')).toBeInTheDocument()
    expect(screen.queryByText('仪表盘')).toBeNull()
    expect(screen.queryByText('沙箱列表')).toBeNull()
    expect(screen.queryByText('批量创建')).toBeNull()
    expect(screen.queryByText('回填导入')).toBeNull()
    expect(screen.getByText('页面内容')).toBeInTheDocument()
  })
})
