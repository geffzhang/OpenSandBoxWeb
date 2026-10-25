import { Button, Result, Space } from 'antd'
import type { ConsoleMe } from '@/api/console'
import { logout } from './auth'

export default function NoPermission({ me }: { me: ConsoleMe }) {
  const hasVolumeRole =
    me.roles.includes('volume:admin') || me.roles.includes('volume:viewer')

  return (
    <Result
      status="403"
      title="没有控制台访问权限"
      subTitle={
        hasVolumeRole
          ? `账号 ${me.username} 只有卷角色；卷管理界面将在后续版本提供。`
          : `账号 ${me.username} 没有 sandbox:viewer 或 sandbox:admin 角色，请联系管理员。`
      }
      extra={
        <Space>
          <Button onClick={logout}>注销</Button>
        </Space>
      }
    />
  )
}
