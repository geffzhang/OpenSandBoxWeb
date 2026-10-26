import { useState } from 'react'
import { App, Card, Descriptions, Divider, Select, Typography } from 'antd'
import { useAuth } from '@/auth/AuthContext'
import { resolveAuthConfig } from '@/auth/authModel'
import { STORAGE_KEY_REFRESH_INTERVAL } from '@/utils/constants'

const INTERVAL_OPTIONS = [
  { value: 10000, label: '10 秒' },
  { value: 30000, label: '30 秒' },
  { value: 60000, label: '60 秒' },
]

export default function Settings() {
  const { message } = App.useApp()
  const [interval, setIntervalValue] = useState(
    () => Number(localStorage.getItem(STORAGE_KEY_REFRESH_INTERVAL)) || 30000,
  )

  const changeInterval = (value: number) => {
    setIntervalValue(value)
    localStorage.setItem(STORAGE_KEY_REFRESH_INTERVAL, String(value))
    message.success('设置已保存')
  }

  const { me } = useAuth()
  let authConfig: { authority: string; clientId: string } | null = null
  try {
    authConfig = resolveAuthConfig()
  } catch {
    authConfig = null
  }

  return (
    <Card title="设置" style={{ maxWidth: 640 }}>
      <Typography.Title level={5}>刷新间隔</Typography.Title>
      <Typography.Paragraph type="secondary">
        沙箱列表自动刷新间隔（保存后对后续打开生效）。
      </Typography.Paragraph>
      <Select style={{ width: 200 }} value={interval} options={INTERVAL_OPTIONS} onChange={changeInterval} />

      <Divider />

      <Typography.Title level={5}>API 端点配置</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="管理 API">/api/v1（由后端服务托管）</Descriptions.Item>
        <Descriptions.Item label="指标流">
          /api/v1/sandboxes/:id/metrics/watch（SSE）
        </Descriptions.Item>
      </Descriptions>

      <Divider />

      <Typography.Title level={5}>当前账号</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="用户名">{me.username}</Descriptions.Item>
        <Descriptions.Item label="Subject">{me.subject}</Descriptions.Item>
        <Descriptions.Item label="角色">{me.roles.join('、') || '无'}</Descriptions.Item>
        <Descriptions.Item label="Keycloak Realm">
          {authConfig?.authority ?? '未配置'}
        </Descriptions.Item>
        <Descriptions.Item label="客户端 ID">
          {authConfig?.clientId ?? '未配置'}
        </Descriptions.Item>
      </Descriptions>

      <Divider />

      <Typography.Title level={5}>关于</Typography.Title>
      <Descriptions column={1} bordered size="small">
        <Descriptions.Item label="版本">1.0.0</Descriptions.Item>
        <Descriptions.Item label="项目地址">
          <Typography.Link href="https://github.com/alibaba/OpenSandbox" target="_blank">
            https://github.com/alibaba/OpenSandbox
          </Typography.Link>
        </Descriptions.Item>
      </Descriptions>
    </Card>
  )
}
