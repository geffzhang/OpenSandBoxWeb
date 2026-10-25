import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { App, Button, Card, Input, List, Progress, Space, Table, Tabs, Typography } from 'antd'
import type { TableProps } from 'antd'
import { InboxOutlined, PlusOutlined } from '@ant-design/icons'
import { batchCreateSandboxes } from '@/utils/batchCreate'
import type { CreateSandboxRequest } from '@/api/types'
import { validateImageUri } from '@/utils/validators'

interface BatchCreateItem {
  id: string
  image: string
  name?: string
  timeout?: number
  cpu?: string
  memory?: string
  entrypoint?: string[]
  env?: Record<string, string>
  metadata?: Record<string, string>
}

interface BatchResultView {
  successful: { image: string; id: string }[]
  failed: { image: string; error: string }[]
}

const toRequest = (item: BatchCreateItem): CreateSandboxRequest => ({
  image: { uri: item.image.trim() },
  entrypoint: item.entrypoint ?? [],
  resourceLimits: Object.fromEntries(
    Object.entries({ cpu: item.cpu ?? '', memory: item.memory ?? '' }).filter(([, v]) => v.trim()),
  ),
  timeout: item.timeout ?? null,
  env: item.env,
  metadata: item.metadata ?? (item.name ? { name: item.name } : undefined),
})

function parseJsonItems(text: string): BatchCreateItem[] | null {
  if (!text.trim()) return null
  try {
    const parsed: unknown = JSON.parse(text)
    if (!Array.isArray(parsed)) return null
    return parsed.map((raw, index) => {
      const entry = (raw ?? {}) as Record<string, unknown>
      return {
        id: `${Date.now()}-${index}`,
        image: String(entry.image ?? ''),
        name: entry.name != null ? String(entry.name) : undefined,
        timeout: typeof entry.timeout === 'number' ? entry.timeout : undefined,
        cpu: entry.cpu != null ? String(entry.cpu) : undefined,
        memory: entry.memory != null ? String(entry.memory) : undefined,
        entrypoint: Array.isArray(entry.entrypoint) ? entry.entrypoint.map(String) : undefined,
        env:
          entry.env && typeof entry.env === 'object'
            ? (entry.env as Record<string, string>)
            : undefined,
        metadata:
          entry.metadata && typeof entry.metadata === 'object'
            ? (entry.metadata as Record<string, string>)
            : undefined,
      }
    })
  } catch {
    return null
  }
}

export default function BatchCreate() {
  const navigate = useNavigate()
  const { message } = App.useApp()
  const [jsonText, setJsonText] = useState('')
  const [items, setItems] = useState<BatchCreateItem[]>([])
  const [progress, setProgress] = useState<{ completed: number; total: number } | null>(null)
  const [result, setResult] = useState<BatchResultView | null>(null)

  const invalidItems = useMemo(
    () => items.filter((item) => validateImageUri(item.image) !== null),
    [items],
  )

  const importJson = () => {
    const parsed = parseJsonItems(jsonText)
    if (parsed === null) {
      message.error('JSON 格式无效：需要包含沙箱对象数组')
      return
    }
    setItems(parsed)
    setResult(null)
    message.success(`已导入 ${parsed.length} 条配置`)
  }

  const addRow = () => {
    setItems((prev) => [...prev, { id: `manual-${prev.length}-${Date.now()}`, image: '' }])
    setResult(null)
  }

  const updateItem = (id: string, patch: Partial<BatchCreateItem>) => {
    setItems((prev) => prev.map((item) => (item.id === id ? { ...item, ...patch } : item)))
  }

  const removeItem = (id: string) => {
    setItems((prev) => prev.filter((item) => item.id !== id))
  }

  const startBatch = async () => {
    if (items.length === 0) {
      message.warning('请先导入或添加沙箱配置')
      return
    }
    if (invalidItems.length > 0) {
      message.error(`${invalidItems.length} 条配置镜像地址无效`)
      return
    }
    setProgress({ completed: 0, total: items.length })
    setResult(null)
    // Build each request exactly once: batchCreateSandboxes returns these same
    // object references in outcome.*.request, so the Map below resolves rows by identity.
    const pending = items.map((item) => ({ item, request: toRequest(item) }))
    const outcome = await batchCreateSandboxes(
      pending.map((entry) => entry.request),
      {
        onProgress: (completed, total) => setProgress({ completed, total }),
      },
    )
    setResult({
      successful: outcome.successful.map((entry) => ({
        image: entry.request.image.uri,
        id: entry.id,
      })),
      failed: outcome.failed.map((entry) => ({
        image: entry.request.image.uri,
        error: entry.error,
      })),
    })
    const requestToItem = new Map(pending.map((entry) => [entry.request, entry.item] as const))
    const failedIds = new Set(
      outcome.failed
        .map((entry) => requestToItem.get(entry.request)?.id)
        .filter((id): id is string => Boolean(id)),
    )
    setItems((prev) => prev.filter((item) => failedIds.has(item.id)))
    setProgress(null)
    if (outcome.failed.length === 0) {
      message.success(`批量创建完成：成功 ${outcome.successful.length}，失败 0`)
    } else {
      message.warning(`批量创建完成：成功 ${outcome.successful.length}，失败 ${outcome.failed.length}`)
    }
  }

  const columns: TableProps<BatchCreateItem>['columns'] = [
    {
      title: '镜像',
      dataIndex: 'image',
      key: 'image',
      render: (image: string, record) => (
        <Input
          value={image}
          status={validateImageUri(image) ? 'error' : ''}
          onChange={(event) => updateItem(record.id, { image: event.target.value })}
        />
      ),
    },
    {
      title: '名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string | undefined, record) => (
        <Input
          value={name}
          onChange={(event) => updateItem(record.id, { name: event.target.value || undefined })}
        />
      ),
    },
    {
      title: 'CPU',
      dataIndex: 'cpu',
      key: 'cpu',
      width: 110,
      render: (cpu: string | undefined, record) => (
        <Input
          value={cpu}
          placeholder="500m"
          onChange={(event) => updateItem(record.id, { cpu: event.target.value || undefined })}
        />
      ),
    },
    {
      title: '内存',
      dataIndex: 'memory',
      key: 'memory',
      width: 110,
      render: (memory: string | undefined, record) => (
        <Input
          value={memory}
          placeholder="512Mi"
          onChange={(event) => updateItem(record.id, { memory: event.target.value || undefined })}
        />
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_, record) => (
        <Button danger size="small" onClick={() => removeItem(record.id)}>
          移除
        </Button>
      ),
    },
  ]

  return (
    <Space direction="vertical" size="large" style={{ display: 'flex' }}>
      <Card title="批量创建沙箱">
        <Tabs
          items={[
            {
              key: 'json',
              label: 'JSON 导入',
              children: (
                <Space direction="vertical" style={{ width: '100%' }}>
                  <Input.TextArea
                    rows={10}
                    value={jsonText}
                    onChange={(event) => setJsonText(event.target.value)}
                    placeholder={'[\n  { "image": "ubuntu:24.04", "name": "demo", "cpu": "500m", "memory": "512Mi", "entrypoint": ["/bin/sh"] }\n]'}
                  />
                  <Button type="primary" icon={<InboxOutlined />} onClick={importJson}>
                    导入
                  </Button>
                </Space>
              ),
            },
            {
              key: 'manual',
              label: '手动添加',
              children: (
                <Button type="dashed" icon={<PlusOutlined />} onClick={addRow}>
                  添加一行
                </Button>
              ),
            },
          ]}
        />
      </Card>

      <Card
        title={`配置预览（${items.length} 条，${invalidItems.length} 条镜像无效）`}
        extra={
          <Space>
            {progress && (
              <Progress
                percent={Math.round((progress.completed / progress.total) * 100)}
                style={{ width: 200 }}
                status="active"
              />
            )}
            <Button
              type="primary"
              onClick={startBatch}
              disabled={items.length === 0 || invalidItems.length > 0 || progress !== null}
            >
              开始创建
            </Button>
          </Space>
        }
      >
        <Table rowKey="id" columns={columns} dataSource={items} pagination={false} size="small" />
      </Card>

      {result && (
        <Card title="创建结果">
          <Typography.Paragraph>
            成功 {result.successful.length} 个，失败 {result.failed.length} 个
          </Typography.Paragraph>
          <List
            size="small"
            dataSource={result.successful}
            renderItem={(entry) => (
              <List.Item>
                <Space>
                  <Typography.Text>{entry.image}</Typography.Text>
                  <Typography.Link onClick={() => navigate(`/sandboxes/${entry.id}`)}>
                    {entry.id}
                  </Typography.Link>
                </Space>
              </List.Item>
            )}
          />
          {result.failed.length > 0 && (
            <List
              size="small"
              header={<Typography.Text type="danger">失败项</Typography.Text>}
              dataSource={result.failed}
              renderItem={(entry) => (
                <List.Item>
                  <Space>
                    <Typography.Text>{entry.image}</Typography.Text>
                    <Typography.Text type="danger">{entry.error}</Typography.Text>
                  </Space>
                </List.Item>
              )}
            />
          )}
        </Card>
      )}
    </Space>
  )
}
