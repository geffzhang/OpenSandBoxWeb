import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  App,
  Button,
  Card,
  Col,
  DatePicker,
  Descriptions,
  Modal,
  Progress,
  Result,
  Row,
  Space,
  Tag,
  Typography,
} from 'antd'
import {
  ClockCircleOutlined,
  DeleteOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
} from '@ant-design/icons'
import { useQueryClient } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { Metrics } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import MetricsChart from '@/components/metrics/MetricsChart'
import { useMetricsStream } from '@/hooks/useMetricsStream'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import { DETAIL_REFRESH_INTERVAL_MS, METRICS_HISTORY_LIMIT } from '@/utils/constants'

export default function SandboxDetail() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [renewOpen, setRenewOpen] = useState(false)
  const [renewAt, setRenewAt] = useState<dayjs.Dayjs | null>(null)
  const [renewing, setRenewing] = useState(false)
  const [history, setHistory] = useState<Metrics[]>([])
  const [prevId, setPrevId] = useState(id)
  if (prevId !== id) {
    setPrevId(id)
    setHistory([])
  }

  const { data: sandbox, isFetching, isError, error } = usePolling(
    ['sandbox', id],
    () => sandboxApi.get(id),
    DETAIL_REFRESH_INTERVAL_MS,
    Boolean(id),
  )

  const isRunning = sandbox?.status.state === 'Running'

  useMetricsStream(
    id,
    (event) => {
      const sample = event.data
      if (event.type === 'metrics' && sample) {
        setHistory((prev) => [...prev.slice(-(METRICS_HISTORY_LIMIT - 1)), sample])
      }
    },
    isRunning,
  )

  const latest = history[history.length - 1]
  const memoryPct =
    latest && latest.memTotalMib > 0
      ? Math.min(100, Math.round((latest.memUsedMib / latest.memTotalMib) * 100))
      : 0

  const runAction = async (action: 'pause' | 'resume' | 'delete', label: string) => {
    try {
      if (action === 'delete') await sandboxApi.remove(id)
      if (action === 'pause') await sandboxApi.pause(id)
      if (action === 'resume') await sandboxApi.resume(id)
      message.success(`已${label}`)
      if (action !== 'delete') queryClient.invalidateQueries({ queryKey: ['sandbox', id] })
      if (action === 'delete') navigate('/sandboxes')
    } catch (error) {
      message.error(extractErrorMessage(error))
    }
  }

  const confirmDelete = () => {
    modal.confirm({
      title: '删除沙箱',
      content: `确定删除沙箱 ${id}？该操作不可恢复。`,
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => runAction('delete', '删除'),
    })
  }

  const submitRenew = async () => {
    if (!renewAt) return
    setRenewing(true)
    try {
      await sandboxApi.renewExpiration(id, { expiresAt: renewAt.toISOString() })
      message.success('过期时间已更新')
      setRenewOpen(false)
      setRenewAt(null)
      queryClient.invalidateQueries({ queryKey: ['sandbox', id] })
    } catch (error) {
      message.error(extractErrorMessage(error))
    } finally {
      setRenewing(false)
    }
  }

  if (!sandbox) {
    if (isFetching) return <Card loading />
    if (isError) {
      return (
        <Result
          status="error"
          title="加载失败"
          subTitle={extractErrorMessage(error)}
          extra={<Button onClick={() => navigate('/sandboxes')}>返回列表</Button>}
        />
      )
    }
    return null
  }

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Card
        title={
          <Space>
            <Typography.Text strong>{sandbox.id}</Typography.Text>
            <StatusBadge state={sandbox.status.state} />
          </Space>
        }
        extra={
          <Space>
            {isRunning ? (
              <Button icon={<PauseCircleOutlined />} onClick={() => runAction('pause', '暂停')}>
                暂停
              </Button>
            ) : (
              <Button icon={<PlayCircleOutlined />} onClick={() => runAction('resume', '恢复')}>
                恢复
              </Button>
            )}
            <Button icon={<ClockCircleOutlined />} onClick={() => setRenewOpen(true)}>
              续期
            </Button>
            <Button danger icon={<DeleteOutlined />} onClick={confirmDelete}>
              删除
            </Button>
          </Space>
        }
      >
        <Descriptions column={2} bordered size="small">
          <Descriptions.Item label="镜像">{sandbox.image.uri}</Descriptions.Item>
          <Descriptions.Item label="入口命令">
            {sandbox.entrypoint.join(' ') || '-'}
          </Descriptions.Item>
          <Descriptions.Item label="创建时间">{formatTime(sandbox.createdAt)}</Descriptions.Item>
          <Descriptions.Item label="过期时间">{formatTime(sandbox.expiresAt)}</Descriptions.Item>
          <Descriptions.Item label="状态说明">
            {sandbox.status.message || sandbox.status.reason || '-'}
          </Descriptions.Item>
          <Descriptions.Item label="元数据">
            {Object.entries(sandbox.metadata ?? {}).map(([key, value]) => (
              <Tag key={key}>{`${key}=${value}`}</Tag>
            ))}
          </Descriptions.Item>
        </Descriptions>
      </Card>

      {isRunning && (
        <Card title="实时监控">
          <Row gutter={16} style={{ marginBottom: 16 }}>
            <Col span={8}>
              <Progress
                type="dashboard"
                percent={latest ? Math.min(100, Math.round(latest.cpuUsedPct)) : 0}
                format={(percent) => `CPU ${percent}%`}
              />
            </Col>
            <Col span={8}>
              <Progress type="dashboard" percent={memoryPct} format={(percent) => `内存 ${percent}%`} />
            </Col>
            <Col span={8}>
              <Descriptions column={1} size="small" bordered>
                <Descriptions.Item label="CPU 核数">{latest?.cpuCount ?? '-'}</Descriptions.Item>
                <Descriptions.Item label="内存用量">
                  {latest
                    ? `${latest.memUsedMib.toFixed(0)} / ${latest.memTotalMib.toFixed(0)} MiB`
                    : '-'}
                </Descriptions.Item>
              </Descriptions>
            </Col>
          </Row>
          <MetricsChart
            data={history.map((sample) => ({
              // ECharts time 轴需要毫秒时间戳；秒级输入（< 1e12）归一化
              timestamp: sample.timestamp < 1e12 ? sample.timestamp * 1000 : sample.timestamp,
              cpu: sample.cpuUsedPct,
              memory: sample.memUsedMib,
            }))}
          />
        </Card>
      )}

      <Modal
        title="延长过期时间"
        open={renewOpen}
        onOk={submitRenew}
        confirmLoading={renewing}
        okButtonProps={{ disabled: !renewAt }}
        onCancel={() => {
          setRenewOpen(false)
          setRenewAt(null)
        }}
        okText="确定"
        cancelText="取消"
      >
        <DatePicker
          showTime
          style={{ width: '100%' }}
          value={renewAt}
          onChange={setRenewAt}
          placeholder="选择新的过期时间"
        />
      </Modal>
    </Space>
  )
}
