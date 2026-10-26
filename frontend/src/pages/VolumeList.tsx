import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  Alert,
  App,
  Button,
  Card,
  Input,
  Select,
  Space,
  Table,
  Tag,
  Typography,
} from 'antd'
import type { TableProps } from 'antd'
import { ReloadOutlined, ScanOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeListItem } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import VolumeLifecycleModal from '@/components/volumes/VolumeLifecycleModal'
import { formatTime } from '@/utils/format'
import {
  CLASSIFICATION_COLORS,
  LIFECYCLE_COLORS,
  classificationLabel,
  lifecycleLabel,
} from '@/utils/volumeLabels'

const DEFAULT_PAGE_SIZE = 20
const MAX_PAGE_SIZE = 200

const CLASSIFICATION_OPTIONS = Object.keys(CLASSIFICATION_COLORS).map((value) => ({
  value,
  label: classificationLabel(value),
}))

const LIFECYCLE_OPTIONS = [
  { value: 'business', label: lifecycleLabel('business') },
  { value: 'ephemeral', label: lifecycleLabel('ephemeral') },
]

function sortParam(field: unknown): string {
  switch (field) {
    case 'claimName':
      return 'claimName'
    case 'createdAt':
      return 'createdAt'
    case 'lastSeenAt':
      return 'lastSeenAt'
    case 'tenantName':
      return 'tenant'
    default:
      return 'namespace'
  }
}

export default function VolumeList() {
  const { message } = App.useApp()
  const { can } = useAuth()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const [selected, setSelected] = useState<VolumeListItem | null>(null)

  const page = Math.max(1, Number(params.get('page') ?? '1') || 1)
  const pageSize = Math.min(
    MAX_PAGE_SIZE,
    Math.max(
      1,
      Number(params.get('pageSize') ?? String(DEFAULT_PAGE_SIZE)) || DEFAULT_PAGE_SIZE,
    ),
  )
  const namespace = params.get('namespace') ?? undefined
  const tenant = params.get('tenant') ?? undefined
  const classification = params.get('classification') ?? undefined
  const lifecycle = params.get('lifecycle') ?? undefined
  const q = params.get('q') ?? undefined
  const sort = params.get('sort') ?? 'namespace'
  const order: 'asc' | 'desc' = params.get('order') === 'desc' ? 'desc' : 'asc'
  const mountedParam = params.get('mounted')
  const mounted =
    mountedParam === 'true' ? true : mountedParam === 'false' ? false : undefined

  const updateParams = (patch: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params)
    Object.entries(patch).forEach(([key, value]) => {
      if (value === undefined || value === '') next.delete(key)
      else next.set(key, value)
    })
    setParams(next, { replace: true })
  }

  const listQuery = useQuery({
    queryKey: ['volumes', 'list', params.toString()],
    queryFn: () =>
      volumeApi.list({
        page,
        pageSize,
        namespace,
        tenant,
        classification,
        lifecycle,
        mounted,
        q,
        sort,
        order,
      }),
  })

  const reportQuery = useQuery({
    queryKey: ['volumes', 'report'],
    queryFn: volumeApi.report,
    staleTime: 30_000,
  })

  const scanMutation = useMutation({
    mutationFn: volumeApi.scan,
    onSuccess: (result) => {
      message.success(`扫描完成，共 ${result.volumeCount} 个卷`)
      queryClient.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const columns: TableProps<VolumeListItem>['columns'] = [
    {
      title: '命名空间',
      dataIndex: 'namespace',
      key: 'namespace',
      width: 160,
      sorter: true,
      sortOrder: sort === 'namespace' ? (order === 'asc' ? 'ascend' : 'descend') : null,
    },
    {
      title: 'PVC',
      dataIndex: 'claimName',
      key: 'claimName',
      width: 200,
      sorter: true,
      sortOrder: sort === 'claimName' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (claimName: string, record) => (
        <Link
          to={`/volumes/${encodeURIComponent(record.namespace)}/${encodeURIComponent(claimName)}`}
        >
          {claimName}
        </Link>
      ),
    },
    {
      title: '租户',
      dataIndex: 'tenantName',
      key: 'tenantName',
      width: 140,
      sorter: true,
      sortOrder: sort === 'tenant' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (_value, record) => record.tenantName ?? record.tenant ?? '-',
    },
    {
      title: '分类',
      dataIndex: 'classification',
      key: 'classification',
      width: 130,
      render: (value: string) => (
        <Tag color={CLASSIFICATION_COLORS[value] ?? 'default'}>
          {classificationLabel(value)}
        </Tag>
      ),
    },
    {
      title: '生命周期',
      dataIndex: 'lifecycle',
      key: 'lifecycle',
      width: 110,
      render: (value: string | null | undefined) =>
        value ? (
          <Tag color={LIFECYCLE_COLORS[value] ?? 'default'}>{lifecycleLabel(value)}</Tag>
        ) : (
          '-'
        ),
    },
    {
      title: '状态',
      dataIndex: 'state',
      key: 'state',
      width: 130,
      render: (value: string | null | undefined, record) =>
        value ? `${value}${record.stateReason ? ` (${record.stateReason})` : ''}` : '-',
    },
    {
      title: '挂载',
      dataIndex: 'mounted',
      key: 'mounted',
      width: 80,
      render: (value: boolean | null | undefined) =>
        value === null || value === undefined ? '-' : value ? '是' : '否',
    },
    {
      title: 'Owner',
      key: 'owner',
      width: 160,
      render: (_, record) =>
        record.ownerKind ? `${record.ownerKind}/${record.ownerId ?? '-'}` : '-',
    },
    {
      title: '容量',
      dataIndex: 'capacity',
      key: 'capacity',
      width: 100,
      render: (value: string | null | undefined) => value ?? '-',
    },
    {
      title: '最后观测',
      dataIndex: 'lastSeenAt',
      key: 'lastSeenAt',
      width: 170,
      sorter: true,
      sortOrder: sort === 'lastSeenAt' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null | undefined) => formatTime(value),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      sorter: true,
      sortOrder: sort === 'createdAt' ? (order === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null | undefined) => formatTime(value),
    },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      fixed: 'right',
      render: (_, record) =>
        can('volume:admin') && record.id ? (
          <Button type="link" onClick={() => setSelected(record)}>
            {record.lifecycle === 'business' ? '降级' : '升级'}
          </Button>
        ) : (
          '-'
        ),
    },
  ]

  const handleTableChange: TableProps<VolumeListItem>['onChange'] = (
    pagination,
    _filters,
    sorter,
  ) => {
    const single = Array.isArray(sorter) ? sorter[0] : sorter
    updateParams({
      page: String(pagination.current ?? 1),
      pageSize: String(pagination.pageSize ?? DEFAULT_PAGE_SIZE),
      sort: single?.order ? sortParam(single.field) : undefined,
      order:
        single?.order === 'ascend'
          ? 'asc'
          : single?.order === 'descend'
            ? 'desc'
            : undefined,
    })
  }

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card>
        <Space wrap size={24}>
          <Typography.Text>
            最后扫描：{formatTime(reportQuery.data?.lastScan?.finishedAt)}
          </Typography.Text>
          <Typography.Text>
            标签覆盖率：{reportQuery.data?.coverage.labeledPercent ?? 0}%
          </Typography.Text>
          <Typography.Text>
            登记覆盖率：{reportQuery.data?.coverage.registeredPercent ?? 0}%
          </Typography.Text>
          <Button
            icon={<ReloadOutlined />}
            onClick={() => {
              listQuery.refetch()
              reportQuery.refetch()
            }}
          >
            刷新
          </Button>
          {can('volume:admin') && (
            <Button
              type="primary"
              icon={<ScanOutlined />}
              loading={scanMutation.isPending}
              onClick={() => scanMutation.mutate()}
            >
              立即扫描
            </Button>
          )}
        </Space>
      </Card>

      <Card>
        <Space wrap style={{ marginBottom: 16 }}>
          <Input
            key={`namespace-${namespace ?? ''}`}
            defaultValue={namespace}
            placeholder="命名空间"
            allowClear
            style={{ width: 160 }}
            onPressEnter={(event) =>
              updateParams({
                namespace: event.currentTarget.value.trim() || undefined,
                page: '1',
              })
            }
            onChange={(event) => {
              if (!event.target.value)
                updateParams({ namespace: undefined, page: '1' })
            }}
          />
          <Input
            key={`tenant-${tenant ?? ''}`}
            defaultValue={tenant}
            placeholder="租户 ID"
            allowClear
            style={{ width: 160 }}
            onPressEnter={(event) =>
              updateParams({
                tenant: event.currentTarget.value.trim() || undefined,
                page: '1',
              })
            }
            onChange={(event) => {
              if (!event.target.value) updateParams({ tenant: undefined, page: '1' })
            }}
          />
          <Select
            allowClear
            placeholder="分类"
            style={{ width: 150 }}
            value={classification}
            options={CLASSIFICATION_OPTIONS}
            onChange={(value) =>
              updateParams({ classification: value || undefined, page: '1' })
            }
          />
          <Select
            allowClear
            placeholder="生命周期"
            style={{ width: 140 }}
            value={lifecycle}
            options={LIFECYCLE_OPTIONS}
            onChange={(value) =>
              updateParams({ lifecycle: value || undefined, page: '1' })
            }
          />
          <Select
            allowClear
            placeholder="挂载"
            style={{ width: 120 }}
            value={mountedParam ?? undefined}
            options={[
              { value: 'true', label: '已挂载' },
              { value: 'false', label: '未挂载' },
            ]}
            onChange={(value) =>
              updateParams({ mounted: value || undefined, page: '1' })
            }
          />
          <Input.Search
            key={`q-${q ?? ''}`}
            defaultValue={q}
            allowClear
            placeholder="搜索 PVC / owner / 租户"
            style={{ width: 260 }}
            onSearch={(value) =>
              updateParams({ q: value.trim() || undefined, page: '1' })
            }
          />
          <Button onClick={() => setParams(new URLSearchParams(), { replace: true })}>
            重置
          </Button>
        </Space>

        {listQuery.isError && (
          <Alert
            type="error"
            showIcon
            message={extractErrorMessage(listQuery.error)}
            style={{ marginBottom: 16 }}
          />
        )}

        <Table<VolumeListItem>
          rowKey={(record) => `${record.namespace}/${record.claimName}`}
          loading={listQuery.isLoading || listQuery.isFetching}
          columns={columns}
          dataSource={listQuery.data?.items ?? []}
          scroll={{ x: 1500 }}
          pagination={{
            current: page,
            pageSize,
            total: listQuery.data?.pagination.totalItems ?? 0,
            showSizeChanger: true,
            showTotal: (total) => `共 ${total} 个卷`,
          }}
          onChange={handleTableChange}
        />
      </Card>

      <VolumeLifecycleModal
        volume={selected}
        open={selected !== null}
        onClose={() => setSelected(null)}
        onSubmitted={() => {
          listQuery.refetch()
          reportQuery.refetch()
        }}
      />
    </Space>
  )
}
