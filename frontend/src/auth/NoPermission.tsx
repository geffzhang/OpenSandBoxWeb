import { Button, Result } from 'antd'
import type { ConsoleMe } from '@/api/console'
import { logout } from './auth'

export default function NoPermission({ me }: { me: ConsoleMe }) {
  return (
    <Result
      status="403"
      title="没有控制台访问权限"
      subTitle={`账号 ${me.username} 没有 sandbox 或 volume 角色，请联系管理员。`}
      extra={<Button onClick={logout}>注销</Button>}
    />
  )
}
