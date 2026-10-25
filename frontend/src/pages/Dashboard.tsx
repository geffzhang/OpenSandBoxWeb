import { useNavigate } from 'react-router-dom'
import { Button, Card, Col, Row, Space, Statistic, Table, Typography } from 'antd'
import type { TableProps } from 'antd'
import { PlusOutlined, ThunderboltOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import type { Sandbox } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import { LIST_REFRESH_INTERVAL_MS } from '@/utils/constants'

const STAT_STATES = ['Running', 'Paused', 'Pending', 'Failed'] as const

const COLUMNS: TableProps<Sandbox>['columns'] = [
  { title: 'ID', dataIndex: 'id', key: 'id', ellipsis: true },
  { title: '镜像', dataIndex: ['image', 'uri'], key: 'image', ellipsis: true },
  {
    title: '状态',
    key: 'state',
    width: 120,
    render: (_, record) => <StatusBadge state={record.status.state} />,
  },
  {
    title: '创建时间',
    key: 'createdAt',
    width: 180,
    render: (_, record) => formatTime(record.createdAt),
  },
]

export default function Dashboard() {
  const navigate = useNavigate()
  const { data } = usePolling(
    ['sandboxes', 'dashboard'],
    () => sandboxApi.list({ page: 1, pageSize: 100 }),
    LIST_REFRESH_INTERVAL_MS,
  )

  const items = data?.items ?? []
  const count = (state: string) => items.filter((item) => item.status.state === state).length
  const recent = [...items].slice(0, 5)

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Row gutter={16}>
        {STAT_STATES.map((state) => (
          <Col key={state} xs={12} sm={12} md={6}>
            <Card>
              <Statistic title={state} value={count(state)} />
            </Card>
          </Col>
        ))}
      </Row>

      <Card
        title="快捷操作"
        extra={
          <Space>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/create')}>
              创建沙箱
            </Button>
            <Button icon={<ThunderboltOutlined />} onClick={() => navigate('/batch-create')}>
              批量创建
            </Button>
          </Space>
        }
      >
        <Typography.Text type="secondary">
          通过上方按钮快速创建沙箱，或前往沙箱列表管理已有实例。
        </Typography.Text>
      </Card>

      <Card title="最近创建">
        <Table rowKey="id" columns={COLUMNS} dataSource={recent} pagination={false} size="middle" />
      </Card>
    </Space>
  )
}
