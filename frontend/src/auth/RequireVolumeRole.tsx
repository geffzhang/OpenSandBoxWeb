import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

function RequireRole({ role, children }: { role: string; children: ReactNode }) {
  const { can } = useAuth()
  if (!can(role)) return <Navigate to="/" replace />
  return <>{children}</>
}

export function RequireVolumeViewer({ children }: { children: ReactNode }) {
  return <RequireRole role="volume:viewer">{children}</RequireRole>
}

export function RequireVolumeAdmin({ children }: { children: ReactNode }) {
  return <RequireRole role="volume:admin">{children}</RequireRole>
}
