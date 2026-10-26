import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import type { VolumeListItem } from '@/api/types'
import { volumeApi } from '@/api/volume'
import VolumeLifecycleModal from '../VolumeLifecycleModal'

vi.mock('@/api/volume', () => ({
  volumeApi: { changeLifecycle: vi.fn() },
}))

const volume: VolumeListItem = {
  namespace: 'opensandbox',
  claimName: 'pvc-1',
  tenant: 'goodcrew',
  tenantName: 'GoodCrew',
  lifecycle: 'ephemeral',
  classification: 'registered',
  violations: [],
}

function renderModal() {
  const onClose = vi.fn()
  const onSubmitted = vi.fn()
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <VolumeLifecycleModal
            volume={volume}
            open
            onClose={onClose}
            onSubmitted={onSubmitted}
          />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
  return { onClose, onSubmitted }
}

describe('VolumeLifecycleModal', () => {
  it('warns instead of showing success when the PVC label patch is pending', async () => {
    vi.mocked(volumeApi.changeLifecycle).mockResolvedValue({
      status: 'label-pending',
      labelState: 'pending',
    })
    const { onClose, onSubmitted } = renderModal()

    fireEvent.click(await screen.findByRole('button', { name: /提\s*交/ }))

    expect(await screen.findByText(/标签同步失败/)).toBeInTheDocument()
    await waitFor(() =>
      expect(document.querySelector('.ant-message-warning')).not.toBeNull(),
    )
    expect(document.querySelector('.ant-message-success')).toBeNull()
    expect(screen.queryByText('生命周期已更新')).toBeNull()
    expect(onSubmitted).toHaveBeenCalledTimes(1)
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
