import { createContext, useContext, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { consoleApi, type ConsoleMe } from '@/api/console'
import { FullPageSpin } from './AuthSplash'
import AuthFailure from './AuthFailure'
import NoPermission from './NoPermission'
import { hasConsoleRole } from './roles'

type AuthContextValue = {
  me: ConsoleMe
  can: (role: string) => boolean
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['console', 'me'],
    queryFn: consoleApi.me,
    retry: false,
    staleTime: 5 * 60 * 1000,
  })

  if (isLoading) return <FullPageSpin />
  if (isError || !data) return <AuthFailure error={error} />

  const hasSandboxRole =
    data.roles.includes('sandbox:admin') || data.roles.includes('sandbox:viewer')
  if (!hasSandboxRole) return <NoPermission me={data} />

  return (
    <AuthContext.Provider
      value={{ me: data, can: (role) => hasConsoleRole(data.roles, role) }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth 必须在 AuthProvider 内使用')
  return context
}
