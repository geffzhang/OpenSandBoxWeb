import type { ReactNode } from 'react'
import { createBrowserRouter } from 'react-router-dom'
import RoleHome from '@/auth/RoleHome'
import { RequireVolumeAdmin, RequireVolumeViewer } from '@/auth/RequireVolumeRole'
import { RequireSandboxAdmin } from '@/auth/RequireSandboxAdmin'
import { RequireSandboxViewer } from '@/auth/RequireSandboxViewer'
import AppLayout from '@/components/layout/AppLayout'
import Dashboard from '@/pages/Dashboard'
import SandboxDetail from '@/pages/SandboxDetail'
import SandboxList from '@/pages/SandboxList'
import CreateSandbox from '@/pages/CreateSandbox'
import BatchCreate from '@/pages/BatchCreate'
import Settings from '@/pages/Settings'
import NotFound from '@/pages/NotFound'
import VolumeList from '@/pages/VolumeList'
import VolumeDetail from '@/pages/VolumeDetail'
import VolumeTenants from '@/pages/VolumeTenants'
import VolumeReport from '@/pages/VolumeReport'
import VolumeImport from '@/pages/VolumeImport'

const sandboxViewer = (element: ReactNode) => (
  <RequireSandboxViewer>{element}</RequireSandboxViewer>
)

const volumeViewer = (element: ReactNode) => (
  <RequireVolumeViewer>{element}</RequireVolumeViewer>
)

const volumeAdmin = (element: ReactNode) => (
  <RequireVolumeAdmin>{element}</RequireVolumeAdmin>
)

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <RoleHome /> },
      { path: 'dashboard', element: sandboxViewer(<Dashboard />) },
      { path: 'sandboxes', element: sandboxViewer(<SandboxList />) },
      { path: 'sandboxes/:id', element: sandboxViewer(<SandboxDetail />) },
      {
        path: 'create',
        element: (
          <RequireSandboxAdmin>
            <CreateSandbox />
          </RequireSandboxAdmin>
        ),
      },
      {
        path: 'batch-create',
        element: (
          <RequireSandboxAdmin>
            <BatchCreate />
          </RequireSandboxAdmin>
        ),
      },
      { path: 'volumes', element: volumeViewer(<VolumeList />) },
      {
        path: 'volumes/:namespace/:claimName',
        element: volumeViewer(<VolumeDetail />),
      },
      { path: 'volume-tenants', element: volumeViewer(<VolumeTenants />) },
      { path: 'volume-report', element: volumeViewer(<VolumeReport />) },
      { path: 'volume-import', element: volumeAdmin(<VolumeImport />) },
      { path: 'settings', element: <Settings /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])
