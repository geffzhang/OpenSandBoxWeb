import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { useAuth } from '@/auth/AuthContext'
import { RequireSandboxViewer } from '../RequireSandboxViewer'

vi.mock('@/auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

const mockedUseAuth = vi.mocked(useAuth)

function renderGuarded() {
  const router = createMemoryRouter(
    [
      { path: '/', element: <div>home-page</div> },
      {
        path: '/dashboard',
        element: (
          <RequireSandboxViewer>
            <div>dashboard-page</div>
          </RequireSandboxViewer>
        ),
      },
    ],
    { initialEntries: ['/dashboard'] },
  )
  return render(<RouterProvider router={router} />)
}

function auth(can: (role: string) => boolean) {
  return {
    me: { subject: 'user-1', username: 'alice', roles: [] },
    can,
  }
}

describe('RequireSandboxViewer', () => {
  beforeEach(() => mockedUseAuth.mockReset())

  it('renders sandbox pages for sandbox viewers', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'sandbox:viewer'))

    renderGuarded()

    expect(await screen.findByText('dashboard-page')).toBeInTheDocument()
  })

  it('redirects volume-only users home', async () => {
    mockedUseAuth.mockReturnValue(auth((role) => role === 'volume:viewer'))

    renderGuarded()

    expect(await screen.findByText('home-page')).toBeInTheDocument()
  })
})
