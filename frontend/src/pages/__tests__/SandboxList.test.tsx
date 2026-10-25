import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import SandboxList from '../SandboxList'

vi.mock('@/api/sandbox', () => ({
  sandboxApi: {
    list: vi.fn().mockResolvedValue({
      items: [
        {
          id: 'sb-1',
          image: { uri: 'ubuntu:24.04' },
          status: { state: 'Running' },
          entrypoint: [],
          createdAt: '2026-01-01T00:00:00Z',
        },
        {
          id: 'sb-2',
          image: { uri: 'alpine:3.20' },
          status: { state: 'Paused' },
          entrypoint: [],
          createdAt: '2026-01-02T00:00:00Z',
        },
      ],
      pagination: { page: 1, pageSize: 20, totalItems: 2, totalPages: 1, hasNextPage: false },
    }),
    remove: vi.fn().mockResolvedValue(undefined),
  },
}))

describe('SandboxList', () => {
  it('renders rows returned by the API', async () => {
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

    expect(await screen.findByText('sb-1')).toBeInTheDocument()
    expect(screen.getByText('sb-2')).toBeInTheDocument()
    expect(screen.getByText('Running')).toBeInTheDocument()
  })
})
