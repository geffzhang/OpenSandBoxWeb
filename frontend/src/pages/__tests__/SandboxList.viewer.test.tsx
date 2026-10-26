import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import SandboxList from '../SandboxList'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: () => ({
    me: { subject: 'user-test-viewer', username: 'viewer', roles: ['sandbox:viewer'] },
    can: () => false,
  }),
}))

vi.mock('@/api/sandbox', () => ({
  sandboxApi: {
    list: vi.fn().mockResolvedValue({
      items: [],
      pagination: { page: 1, pageSize: 20, totalItems: 0, totalPages: 0, hasNextPage: false },
    }),
    remove: vi.fn(),
  },
}))

describe('SandboxList as a sandbox viewer', () => {
  it('renders no delete controls and no row selection', async () => {
    const router = createMemoryRouter([{ path: '/', element: <SandboxList /> }], {
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

    expect(await screen.findByText('沙箱列表')).toBeInTheDocument()
    await waitFor(() => expect(document.querySelector('.ant-empty')).not.toBeNull())

    expect(screen.queryByRole('button', { name: /批量删除/ })).toBeNull()
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0)
    expect(document.querySelectorAll('.anticon-delete')).toHaveLength(0)
  })
})
