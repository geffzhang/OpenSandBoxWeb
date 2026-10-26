import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { useAuth } from '@/auth/AuthContext'
import RoleHome from '../RoleHome'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

const mockedUseAuth = vi.mocked(useAuth)

function renderRoot() {
  const router = createMemoryRouter(
    [
      { path: '/', element: <RoleHome /> },
      { path: '/dashboard', element: <div>dashboard-page</div> },
      { path: '/volumes', element: <div>volumes-page</div> },
      { path: '/settings', element: <div>settings-page</div> },
    ],
    { initialEntries: ['/'] },
  )
  return render(<RouterProvider router={router} />)
}

function auth(can: (role: string) => boolean) {
  return {
    me: { subject: 'user-1', username: 'alice', roles: [] },
    can,
  }
}

describe('RoleHome', () => {
  beforeEach(() => mockedUseAuth.mockReset())

  it('sends sandbox users to the dashboard', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'sandbox:viewer'))

    renderRoot()

    expect(await screen.findByText('dashboard-page')).toBeInTheDocument()
  })

  it('sends volume-only users to the volume list', async () => {
    mockedUseAuth.mockReturnValue(
      auth((role) => role === 'volume:viewer' || role === 'volume:admin'),
    )

    renderRoot()

    expect(await screen.findByText('volumes-page')).toBeInTheDocument()
  })
})
