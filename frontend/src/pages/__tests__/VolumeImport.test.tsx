import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import { volumeApi } from '@/api/volume'
import VolumeImport from '../VolumeImport'

vi.mock('@/api/volume', () => ({
  volumeApi: { import: vi.fn() },
}))

const CSV = [
  'tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId',
  'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,/workspace,business,sb-1',
].join('\n')

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <ConfigProvider>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <VolumeImport />
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>,
  )
}

describe('VolumeImport', () => {
  beforeEach(() => {
    vi.mocked(volumeApi.import).mockResolvedValue({
      dryRun: true,
      results: [
        {
          namespace: 'opensandbox',
          claimName: 'pvc-1',
          status: 'would-register',
        },
      ],
    })
  })

  it('parses CSV and calls dry-run before allowing write', async () => {
    renderPage()

    fireEvent.change(screen.getByPlaceholderText(/粘贴 CSV/), {
      target: { value: CSV },
    })
    fireEvent.click(screen.getByRole('button', { name: '解析预览' }))

    expect(await screen.findByText('pvc-1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确认写入' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: 'Dry run' }))

    await waitFor(() =>
      expect(volumeApi.import).toHaveBeenCalledWith({
        dryRun: true,
        items: [expect.objectContaining({ claimName: 'pvc-1' })],
      }),
    )
  })

  it('shows parse errors without calling the API', () => {
    renderPage()

    fireEvent.change(screen.getByPlaceholderText(/粘贴 CSV/), {
      target: { value: 'tenant,namespace\nbad,row' },
    })
    fireEvent.click(screen.getByRole('button', { name: '解析预览' }))

    expect(screen.getByText(/缺少表头/)).toBeInTheDocument()
    expect(volumeApi.import).not.toHaveBeenCalled()
  })
})
