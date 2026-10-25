import { Button, Result, Space } from 'antd'
import { extractErrorMessage } from '@/api/client'
import { login, logout } from './auth'

export default function AuthFailure({ error }: { error: unknown }) {
  return (
    <Result
      status="error"
      title="无法完成登录"
      subTitle={extractErrorMessage(error)}
      extra={
        <Space>
          <Button type="primary" onClick={() => login()}>
            重新登录
          </Button>
          <Button onClick={logout}>注销</Button>
        </Space>
      }
    />
  )
}
