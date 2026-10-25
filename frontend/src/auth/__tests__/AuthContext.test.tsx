import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import { consoleApi, type ConsoleMe } from '@/api/console'
import { AuthProvider } from '../AuthContext'

vi.mock('@/api/console', () => ({
  consoleApi: { me: vi.fn() },
}))

const me = (roles: string[]): ConsoleMe => ({ subject: 'user-1', username: 'tester', roles })

function renderAuth(roles: string[]) {
  vi.mocked(consoleApi.me).mockResolvedValueOnce(me(roles))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  return render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <AuthProvider>
            <div>控制台内容</div>
          </AuthProvider>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('AuthProvider', () => {
  it('shows the no-role message when the account has no console roles', async () => {
    renderAuth([])

    expect(
      await screen.findByText(/没有 sandbox:viewer 或 sandbox:admin 角色/),
    ).toBeInTheDocument()
  })

  it('shows the volume-only variant for volume roles', async () => {
    renderAuth(['volume:viewer'])

    expect(await screen.findByText(/只有卷角色/)).toBeInTheDocument()
  })

  it('renders children for sandbox viewers', async () => {
    renderAuth(['sandbox:viewer'])

    expect(await screen.findByText('控制台内容')).toBeInTheDocument()
  })
})
