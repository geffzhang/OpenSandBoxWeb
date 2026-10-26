import { useEffect, useState } from 'react'
import { Alert, App, Descriptions, Input, Modal, Select, Space, Tag, Typography } from 'antd'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeListItem } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { lifecycleLabel } from '@/utils/volumeLabels'

interface VolumeLifecycleModalProps {
  volume: VolumeListItem | null
  open: boolean
  onClose: () => void
  onSubmitted: () => void
}

export default function VolumeLifecycleModal({
  volume,
  open,
  onClose,
  onSubmitted,
}: VolumeLifecycleModalProps) {
  const { message } = App.useApp()
  const [target, setTarget] = useState<'business' | 'ephemeral'>('ephemeral')
  const [reason, setReason] = useState('')

  useEffect(() => {
    if (!open || !volume) return
    setTarget(volume.lifecycle === 'business' ? 'ephemeral' : 'business')
    setReason('')
  }, [open, volume])

  const mutation = useMutation({
    mutationFn: () => {
      if (!volume) throw new Error('未选择卷')
      return volumeApi.changeLifecycle(volume.namespace, volume.claimName, {
        lifecycle: target,
        reason: target === 'ephemeral' ? reason.trim() : undefined,
      })
    },
    onSuccess: (response) => {
      if (response.status === 'label-pending') {
        message.warning(
          '台账已更新，但 PVC 标签同步失败，扫描器会自动重试。删除保护以 PVC 标签为准，标签清除前实际状态可能与台账不一致。',
        )
      } else if (response.status === 'unchanged') {
        message.info('生命周期未变化')
      } else {
        message.success('生命周期已更新')
      }
      onSubmitted()
      onClose()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const reasonRequired = target === 'ephemeral'

  return (
    <Modal
      open={open}
      title="变更卷生命周期"
      okText="提交"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      okButtonProps={{ disabled: reasonRequired && reason.trim().length === 0 }}
      onCancel={onClose}
      onOk={() => mutation.mutate()}
    >
      {volume && (
        <>
          <Descriptions column={1} size="small" style={{ marginBottom: 16 }}>
            <Descriptions.Item label="卷">
              {volume.namespace}/{volume.claimName}
            </Descriptions.Item>
            <Descriptions.Item label="当前生命周期">
              <Tag>{lifecycleLabel(volume.lifecycle ?? '-')}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label="租户">
              {volume.tenantName ?? volume.tenant ?? '-'}
            </Descriptions.Item>
          </Descriptions>

          {reasonRequired && (
            <Alert
              type="warning"
              showIcon
              message="降级为临时卷后，Kubernetes 删除保护将不再拦截该 PVC。"
              style={{ marginBottom: 16 }}
            />
          )}

          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <div>
              <Typography.Text>目标生命周期</Typography.Text>
              <Select
                value={target}
                onChange={(value) => setTarget(value as 'business' | 'ephemeral')}
                style={{ width: '100%', marginTop: 8 }}
                options={[
                  { value: 'business', label: '业务持久' },
                  { value: 'ephemeral', label: '临时' },
                ]}
              />
            </div>
            <div>
              <Typography.Text>
                原因{reasonRequired ? '（必填）' : '（可选）'}
              </Typography.Text>
              <Input.TextArea
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                rows={3}
                maxLength={512}
                showCount
                placeholder="例如：业务确认该员工工作区不再需要保留"
                style={{ marginTop: 8 }}
              />
            </div>
          </Space>
        </>
      )}
    </Modal>
  )
}
