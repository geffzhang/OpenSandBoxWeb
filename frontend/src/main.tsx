import React, { useEffect, useState, type ReactNode } from 'react'
import ReactDOM from 'react-dom/client'
import { RouterProvider } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App as AntApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { ensureAuthenticatedOnce } from '@/auth/auth'
import { AuthProvider } from '@/auth/AuthContext'
import { FullPageSpin } from '@/auth/AuthSplash'
import AuthFailure from '@/auth/AuthFailure'
import { syncRouterToBrowserUrl } from '@/router/bootSync'
import { router } from '@/router'
import 'antd/dist/reset.css'
import '@/styles/global.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 5000 } },
})

function BootGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<'checking' | 'ready' | 'failed'>('checking')
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let active = true
    ensureAuthenticatedOnce()
      .then(async () => {
        if (!active) return
        // 登录回调用 history.replaceState 恢复了 returnTo，但路由单例在模块
        // 加载时已创建，仍指向回调地址；这里把路由对齐到当前浏览器 URL。
        await syncRouterToBrowserUrl(router)
        if (active) setPhase('ready')
      })
      .catch((err: unknown) => {
        if (!active) return
        setError(err)
        setPhase('failed')
      })
    return () => {
      active = false
    }
  }, [])

  if (phase === 'failed') return <AuthFailure error={error} />
  if (phase === 'checking') return <FullPageSpin />
  return <>{children}</>
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider locale={zhCN}>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <BootGate>
            <AuthProvider>
              <RouterProvider router={router} />
            </AuthProvider>
          </BootGate>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  </React.StrictMode>,
)
