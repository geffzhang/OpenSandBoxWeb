import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { App as AntApp, ConfigProvider } from 'antd'
import AppLayout from '../AppLayout'

describe('AppLayout', () => {
  it('renders all navigation items and page content', () => {
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

    render(
      <ConfigProvider>
        <AntApp>
          <RouterProvider router={router} />
        </AntApp>
      </ConfigProvider>,
    )

    expect(screen.getByText('OpenSandbox Admin')).toBeInTheDocument()
    expect(screen.getByText('仪表盘')).toBeInTheDocument()
    expect(screen.getByText('沙箱列表')).toBeInTheDocument()
    expect(screen.getByText('批量创建')).toBeInTheDocument()
    expect(screen.getByText('设置')).toBeInTheDocument()
    expect(screen.getByText('页面内容')).toBeInTheDocument()
  })
})
