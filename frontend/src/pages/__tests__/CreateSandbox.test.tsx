import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import CreateSandbox from '../CreateSandbox'
import { sandboxApi } from '@/api/sandbox'

vi.mock('@/api/sandbox', () => ({
  sandboxApi: { create: vi.fn().mockResolvedValue({ id: 'sb-new' }) },
}))

describe('CreateSandbox', () => {
  it('submits a valid image while skipping a blank dynamic row', async () => {
    const router = createMemoryRouter(
      [
        { path: '/', element: <CreateSandbox /> },
        { path: '/sandboxes/:id', element: <div>详情页</div> },
      ],
      { initialEntries: ['/'] },
    )

    render(
      <ConfigProvider>
        <AntApp>
          <RouterProvider router={router} />
        </AntApp>
      </ConfigProvider>,
    )

    const user = userEvent.setup()
    await user.type(screen.getByPlaceholderText(/ubuntu:24.04/), 'ubuntu:24.04')
    await user.click(screen.getByRole('button', { name: /添加环境变量/ }))
    await user.click(screen.getByRole('button', { name: '创建沙箱' }))

    await waitFor(() => expect(sandboxApi.create).toHaveBeenCalledTimes(1))
    expect(sandboxApi.create).toHaveBeenCalledWith(
      expect.objectContaining({ image: { uri: 'ubuntu:24.04' }, env: undefined }),
    )
    expect(await screen.findByText('详情页')).toBeInTheDocument()
  })
})
