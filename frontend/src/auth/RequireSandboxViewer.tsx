import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function RequireSandboxViewer({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  if (!can('sandbox:viewer')) return <Navigate to="/" replace />
  return <>{children}</>
}
