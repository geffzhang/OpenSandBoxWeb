import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

export default function RoleHome() {
  const { can } = useAuth()
  if (can('sandbox:viewer')) return <Navigate to="/dashboard" replace />
  if (can('volume:viewer')) return <Navigate to="/volumes" replace />
  return <Navigate to="/settings" replace />
}
