import { useState } from 'react'
import {
  Alert,
  App,
  Button,
  Divider,
  Empty,
  Form,
  Input,
  List,
  Modal,
  Space,
  Tag,
  Typography,
} from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { formatTime } from '@/utils/format'

interface TenantKeysModalProps {
  tenant: VolumeTenant | null
  open: boolean
  canManage: boolean
  onClose: () => void
}

export default function TenantKeysModal({
  tenant,
  open,
  canManage,
  onClose,
}: TenantKeysModalProps) {
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [rawKey, setRawKey] = useState<string | null>(null)
  const tenantId = tenant?.id ?? ''

  const keysQuery = useQuery({
    queryKey: ['volume-tenants', tenantId, 'keys'],
    queryFn: () => volumeApi.keys(tenantId),
    enabled: open && tenantId.length > 0,
  })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['volume-tenants'] })
    void keysQuery.refetch()
  }

  const createMutation = useMutation({
    mutationFn: () =>
      volumeApi.createKey(tenantId, { name: name.trim() || 'default' }),
    onSuccess: (created) => {
      setRawKey(created.key)
      setName('')
      refresh()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const revokeMutation = useMutation({
    mutationFn: (keyId: string) => volumeApi.revokeKey(tenantId, keyId),
    onSuccess: () => {
      message.success('Key 已吊销')
      refresh()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const confirmRevoke = (keyId: string, prefix: string) => {
    modal.confirm({
      title: '吊销 Key',
      content: `确定吊销 ${prefix} 开头的 Key？调用方将立即失效。`,
      okText: '吊销',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => revokeMutation.mutateAsync(keyId),
    })
  }

  return (
    <Modal
      open={open}
      title={`租户 Key - ${tenant?.displayName ?? ''}`}
      footer={null}
      width={720}
      onCancel={() => {
        setRawKey(null)
        onClose()
      }}
    >
      {rawKey && (
        <Alert
          type="success"
          showIcon
          message="新 Key 只显示一次"
          description={
            <Typography.Paragraph code copyable={{ text: rawKey }} style={{ margin: 0 }}>
              {rawKey}
            </Typography.Paragraph>
          }
          action={
            <Button size="small" onClick={() => setRawKey(null)}>
              我已保存
            </Button>
          }
          style={{ marginBottom: 16 }}
        />
      )}

      {canManage && (
        <Form layout="inline" onFinish={() => createMutation.mutate()} style={{ marginBottom: 16 }}>
          <Form.Item>
            <Input
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="Key 名称（如 ci）"
              maxLength={64}
            />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={createMutation.isPending}>
              签发 Key
            </Button>
          </Form.Item>
        </Form>
      )}

      <Divider />

      <List
        loading={keysQuery.isLoading}
        dataSource={keysQuery.data ?? []}
        locale={{ emptyText: <Empty description="暂无 Key" /> }}
        renderItem={(key) => (
          <List.Item
            actions={[
              key.revokedAt ? (
                <Tag key="revoked">已吊销</Tag>
              ) : canManage ? (
                <Button
                  key="revoke"
                  danger
                  type="link"
                  onClick={() => confirmRevoke(key.id, key.prefix)}
                >
                  吊销
                </Button>
              ) : null,
            ]}
          >
            <List.Item.Meta
              title={
                <Space>
                  <Typography.Text code>{key.prefix}…</Typography.Text>
                  <span>{key.name}</span>
                </Space>
              }
              description={`创建 ${formatTime(key.createdAt)} · 最近使用 ${formatTime(key.lastUsedAt)}`}
            />
          </List.Item>
        )}
      />
    </Modal>
  )
}
