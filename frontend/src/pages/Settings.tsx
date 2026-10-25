import { useState } from 'react'
import { App, Card, Descriptions, Divider, Select, Typography } from 'antd'
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
