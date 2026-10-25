import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function RequireSandboxAdmin({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  if (!can('sandbox:admin')) return <Navigate to="/dashboard" replace />
  return <>{children}</>
}
