import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import { volumeApi } from '@/api/volume'
import TenantFormModal from '../TenantFormModal'

vi.mock('@/api/volume', () => ({
  volumeApi: { createTenant: vi.fn(), updateTenant: vi.fn() },
}))

function renderModal() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <TenantFormModal tenant={null} open onClose={vi.fn()} onSaved={vi.fn()} />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('TenantFormModal', () => {
  it('rejects a trailing hyphen and accepts internal hyphens in tenant IDs', async () => {
    renderModal()

    fireEvent.change(screen.getByLabelText('租户 ID'), { target: { value: 'abc-' } })
    fireEvent.change(screen.getByLabelText('显示名'), { target: { value: 'Test' } })
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))

    expect(await screen.findByText('小写字母、数字或连字符')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('租户 ID'), { target: { value: 'abc-def' } })
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))

    await waitFor(() =>
      expect(screen.queryByText('小写字母、数字或连字符')).not.toBeInTheDocument(),
    )
    expect(volumeApi.createTenant).not.toHaveBeenCalled()
  })
})
