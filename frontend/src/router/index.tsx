import { createBrowserRouter, Navigate } from 'react-router-dom'
import AppLayout from '@/components/layout/AppLayout'
import Dashboard from '@/pages/Dashboard'
import SandboxList from '@/pages/SandboxList'
import NotFound from '@/pages/NotFound'

// 占位组件：Task 10-15 逐个替换
const Placeholder = ({ title }: { title: string }) => (
  <div style={{ padding: 24 }}>{title}（待实现）</div>
)

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <Navigate to="/dashboard" replace /> },
      { path: 'dashboard', element: <Dashboard /> },
      { path: 'sandboxes', element: <SandboxList /> },
      { path: 'sandboxes/:id', element: <Placeholder title="沙箱详情" /> },
      { path: 'create', element: <Placeholder title="创建沙箱" /> },
      { path: 'batch-create', element: <Placeholder title="批量创建" /> },
      { path: 'settings', element: <Placeholder title="设置" /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])
