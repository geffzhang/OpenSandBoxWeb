import { useState, type Key } from 'react'
import { useNavigate } from 'react-router-dom'
import { App, Button, Card, Input, Select, Space, Table, Typography } from 'antd'
import type { TableProps } from 'antd'
import { DeleteOutlined, ReloadOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { Sandbox } from '@/api/types'
import StatusBadge from '@/components/common/StatusBadge'
import { usePolling } from '@/hooks/usePolling'
import { formatTime } from '@/utils/format'
import {
  DEFAULT_PAGE_SIZE,
  LIST_REFRESH_INTERVAL_MS,
  STORAGE_KEY_REFRESH_INTERVAL,
} from '@/utils/constants'

const STATE_OPTIONS = ['Creating', 'Running', 'Pausing', 'Paused', 'Resuming', 'Deleting', 'Deleted', 'Error']

export default function SandboxList() {
  const navigate = useNavigate()
  const { message, modal } = App.useApp()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [states, setStates] = useState<string[]>([])
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Key[]>([])

  const refreshInterval =
    Number(localStorage.getItem(STORAGE_KEY_REFRESH_INTERVAL)) || LIST_REFRESH_INTERVAL_MS

  const { data, isFetching, refetch } = usePolling(
    ['sandboxes', 'list', String(page), String(pageSize), states.join(',')],
    () => sandboxApi.list({ page, pageSize, state: states.length ? states : undefined }),
    refreshInterval,
  )

  // 关键词在服务端返回的当前页内过滤（ID + metadata 值）
  const filtered = (data?.items ?? []).filter((item) => {
    if (!search.trim()) return true
    const haystack = [item.id, ...Object.values(item.metadata ?? {})].join(' ')
    return haystack.toLowerCase().includes(search.trim().toLowerCase())
  })

  const confirmDelete = (ids: string[]) => {
    modal.confirm({
      title: '删除沙箱',
      content: `确定删除 ${ids.length} 个沙箱？该操作不可恢复。`,
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: async () => {
        try {
          const results = await Promise.allSettled(ids.map((id) => sandboxApi.remove(id)))
          const failedCount = results.filter((result) => result.status === 'rejected').length
          if (failedCount === 0) message.success(`已删除 ${ids.length} 个沙箱`)
          else message.warning(`删除完成，${failedCount} 个失败`)
          setSelected([])
          refetch()
        } catch (error) {
          message.error(extractErrorMessage(error))
        }
      },
    })
  }

  const columns: TableProps<Sandbox>['columns'] = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      ellipsis: true,
      render: (id: string) => (
        <Typography.Link onClick={() => navigate(`/sandboxes/${id}`)}>{id}</Typography.Link>
      ),
    },
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
    {
      title: '过期时间',
      key: 'expiresAt',
      width: 180,
      render: (_, record) => formatTime(record.expiresAt),
    },
    {
      title: '操作',
      key: 'actions',
      width: 150,
      render: (_, record) => (
        <Space>
          <Button size="small" onClick={() => navigate(`/sandboxes/${record.id}`)}>
            详情
          </Button>
          <Button
            size="small"
            danger
            icon={<DeleteOutlined />}
            onClick={() => confirmDelete([record.id])}
          />
        </Space>
      ),
    },
  ]

  return (
    <Card
      title="沙箱列表"
      extra={
        <Space>
          <Select
            mode="multiple"
            allowClear
            placeholder="按状态筛选"
            style={{ minWidth: 220 }}
            options={STATE_OPTIONS.map((state) => ({ value: state, label: state }))}
            value={states}
            onChange={setStates}
          />
          <Input.Search
            allowClear
            placeholder="搜索 ID 或元数据"
            style={{ width: 220 }}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <Button icon={<ReloadOutlined />} loading={isFetching} onClick={() => refetch()}>
            刷新
          </Button>
          <Button
            danger
            icon={<DeleteOutlined />}
            disabled={selected.length === 0}
            onClick={() => confirmDelete(selected.map(String))}
          >
            批量删除
          </Button>
        </Space>
      }
    >
      <Table
        rowKey="id"
        columns={columns}
        dataSource={filtered}
        loading={isFetching}
        rowSelection={{ selectedRowKeys: selected, onChange: setSelected }}
        pagination={{
          current: page,
          pageSize,
          total: data?.pagination.totalItems ?? 0,
          showSizeChanger: true,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPageSize !== pageSize ? 1 : nextPage)
            setPageSize(nextPageSize)
          },
        }}
      />
    </Card>
  )
}
