import { afterEach, describe, expect, it, vi } from 'vitest'
import { syncRouterToBrowserUrl } from '../bootSync'

function fakeRouter(location: { pathname: string; search?: string; hash?: string }) {
  return {
    state: { location: { search: '', hash: '', ...location } },
    navigate: vi.fn().mockResolvedValue(undefined),
  }
}

afterEach(() => {
  window.history.replaceState(null, '', '/')
})

describe('syncRouterToBrowserUrl', () => {
  it('navigates to the browser URL when the router is stale', async () => {
    window.history.replaceState(null, '', '/sandboxes?tab=1')
    const router = fakeRouter({ pathname: '/dashboard' })

    await syncRouterToBrowserUrl(router)

    expect(router.navigate).toHaveBeenCalledWith('/sandboxes?tab=1', { replace: true })
  })

  it('does nothing when the router already matches the browser URL', async () => {
    window.history.replaceState(null, '', '/sandboxes?tab=1#top')
    const router = fakeRouter({ pathname: '/sandboxes', search: '?tab=1', hash: '#top' })

    await syncRouterToBrowserUrl(router)

    expect(router.navigate).not.toHaveBeenCalled()
  })

  it('propagates navigation failures so boot can fail closed', async () => {
    window.history.replaceState(null, '', '/sandboxes')
    const router = fakeRouter({ pathname: '/dashboard' })
    router.navigate.mockRejectedValueOnce(new Error('导航失败'))

    await expect(syncRouterToBrowserUrl(router)).rejects.toThrow('导航失败')
  })
})
