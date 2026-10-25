type BrowserLocation = {
  pathname: string
  search: string
  hash: string
}

export type RouterLike = {
  state: { location: BrowserLocation }
  navigate: (to: string, options?: { replace?: boolean }) => Promise<void>
}

/**
 * auth.ts 在登录回调里用 history.replaceState 恢复 returnTo，
 * 但路由单例在模块加载时就已创建，仍停留在回调地址。
 * 启动门控在认证完成后调用本函数，把路由对齐到当前浏览器 URL。
 */
export async function syncRouterToBrowserUrl(router: RouterLike): Promise<void> {
  const target = `${window.location.pathname}${window.location.search}${window.location.hash}`
  const current = `${router.state.location.pathname}${router.state.location.search}${router.state.location.hash}`
  if (target === current) return
  await router.navigate(target, { replace: true })
}
