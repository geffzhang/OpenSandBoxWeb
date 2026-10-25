import { createBrowserRouter, Navigate } from 'react-router-dom'
import AppLayout from '@/components/layout/AppLayout'
import Dashboard from '@/pages/Dashboard'
import SandboxDetail from '@/pages/SandboxDetail'
import SandboxList from '@/pages/SandboxList'
import CreateSandbox from '@/pages/CreateSandbox'
import BatchCreate from '@/pages/BatchCreate'
import Settings from '@/pages/Settings'
import NotFound from '@/pages/NotFound'

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <Navigate to="/dashboard" replace /> },
      { path: 'dashboard', element: <Dashboard /> },
      { path: 'sandboxes', element: <SandboxList /> },
      { path: 'sandboxes/:id', element: <SandboxDetail /> },
      { path: 'create', element: <CreateSandbox /> },
      { path: 'batch-create', element: <BatchCreate /> },
      { path: 'settings', element: <Settings /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])
