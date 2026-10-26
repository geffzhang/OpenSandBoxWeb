import { useState } from 'react'
import { Button, Card, Space, Table, Tag } from 'antd'
import type { TableProps } from 'antd'
import { KeyOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import TenantFormModal from '@/components/volumes/TenantFormModal'
import TenantKeysModal from '@/components/volumes/TenantKeysModal'
import { formatTime } from '@/utils/format'

export default function VolumeTenants() {
  const { can } = useAuth()
  const [formTenant, setFormTenant] = useState<VolumeTenant | null>(null)
  const [formOpen, setFormOpen] = useState(false)
  const [keysTenant, setKeysTenant] = useState<VolumeTenant | null>(null)
  const canManage = can('volume:admin')

  const tenantsQuery = useQuery({
    queryKey: ['volume-tenants'],
    queryFn: volumeApi.tenants,
  })

  const columns: TableProps<VolumeTenant>['columns'] = [
    { title: '租户 ID', dataIndex: 'id', key: 'id', width: 150 },
    { title: '显示名', dataIndex: 'displayName', key: 'displayName', width: 160 },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 90,
      render: (value: string) => (
        <Tag color={value === 'active' ? 'green' : 'red'}>
          {value === 'active' ? '启用' : '停用'}
        </Tag>
      ),
    },
    {
      title: '命名空间',
      dataIndex: 'namespaces',
      key: 'namespaces',
      render: (values: string[]) => (
        <Space wrap>
          {values.map((value) => (
            <Tag key={value}>{value}</Tag>
          ))}
        </Space>
      ),
    },
    { title: '卷', dataIndex: 'volumeCount', key: 'volumeCount', width: 70 },
    { title: '业务卷', dataIndex: 'protectedCount', key: 'protectedCount', width: 80 },
    { title: '临时卷', dataIndex: 'ephemeralCount', key: 'ephemeralCount', width: 80 },
    { title: '缺失', dataIndex: 'missingCount', key: 'missingCount', width: 70 },
    { title: 'Key', dataIndex: 'keyCount', key: 'keyCount', width: 70 },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      render: (value: string) => formatTime(value),
    },
    {
      title: '操作',
      key: 'actions',
      width: 190,
      render: (_, record) => (
        <Space>
          <Button
            type="link"
            icon={<KeyOutlined />}
            onClick={() => setKeysTenant(record)}
          >
            Key
          </Button>
          {canManage && (
            <Button
              type="link"
              onClick={() => {
                setFormTenant(record)
                setFormOpen(true)
              }}
            >
              编辑
            </Button>
          )}
        </Space>
      ),
    },
  ]

  return (
    <Card
      title="租户管理"
      extra={
        canManage && (
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => {
              setFormTenant(null)
              setFormOpen(true)
            }}
          >
            新建租户
          </Button>
        )
      }
    >
      <Table<VolumeTenant>
        rowKey="id"
        loading={tenantsQuery.isLoading}
        columns={columns}
        dataSource={tenantsQuery.data ?? []}
        scroll={{ x: 1300 }}
        pagination={{ pageSize: 20, showTotal: (total) => `共 ${total} 个租户` }}
        locale={{ emptyText: tenantsQuery.isError ? '加载失败' : '暂无租户' }}
      />

      <TenantFormModal
        tenant={formTenant}
        open={formOpen}
        onClose={() => setFormOpen(false)}
        onSaved={() => tenantsQuery.refetch()}
      />
      <TenantKeysModal
        tenant={keysTenant}
        open={keysTenant !== null}
        canManage={canManage}
        onClose={() => setKeysTenant(null)}
      />
    </Card>
  )
}
