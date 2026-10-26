import { useEffect } from 'react'
import { App, Form, Input, Modal, Select } from 'antd'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeTenant } from '@/api/types'
import { volumeApi } from '@/api/volume'

interface TenantFormModalProps {
  tenant: VolumeTenant | null
  open: boolean
  onClose: () => void
  onSaved: () => void
}

interface FormValues {
  id: string
  displayName: string
  status: 'active' | 'disabled'
  namespaces: string[]
}

export default function TenantFormModal({
  tenant,
  open,
  onClose,
  onSaved,
}: TenantFormModalProps) {
  const { message } = App.useApp()
  const [form] = Form.useForm<FormValues>()

  useEffect(() => {
    if (!open) return
    if (tenant) {
      form.setFieldsValue({
        id: tenant.id,
        displayName: tenant.displayName,
        status: tenant.status === 'disabled' ? 'disabled' : 'active',
        namespaces: tenant.namespaces,
      })
    } else {
      form.resetFields()
    }
  }, [open, tenant, form])

  const mutation = useMutation({
    mutationFn: (values: FormValues) =>
      tenant
        ? volumeApi.updateTenant(tenant.id, {
            displayName: values.displayName,
            status: values.status,
            namespaces: values.namespaces,
          })
        : volumeApi.createTenant({
            id: values.id,
            displayName: values.displayName,
            namespaces: values.namespaces,
          }),
    onSuccess: () => {
      message.success(tenant ? '租户已更新' : '租户已创建')
      onSaved()
      onClose()
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  return (
    <Modal
      open={open}
      title={tenant ? '编辑租户' : '新建租户'}
      okText="保存"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => form.submit()}
    >
      <Form
        form={form}
        layout="vertical"
        initialValues={{ status: 'active', namespaces: [] }}
        onFinish={(values) => mutation.mutate(values)}
      >
        <Form.Item
          name="id"
          label="租户 ID"
          rules={[
            { required: true, message: '请输入租户 ID' },
            {
              pattern: /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/,
              message: '小写字母、数字或连字符',
            },
          ]}
        >
          <Input disabled={tenant !== null} placeholder="goodcrew" />
        </Form.Item>
        <Form.Item
          name="displayName"
          label="显示名"
          rules={[{ required: true, message: '请输入显示名' }]}
        >
          <Input placeholder="GoodCrew" maxLength={128} />
        </Form.Item>
        {tenant && (
          <Form.Item name="status" label="状态">
            <Select
              options={[
                { value: 'active', label: '启用' },
                { value: 'disabled', label: '停用' },
              ]}
            />
          </Form.Item>
        )}
        <Form.Item
          name="namespaces"
          label="命名空间"
          rules={[{ required: true, message: '至少一个命名空间' }]}
        >
          <Select mode="tags" tokenSeparators={[',']} placeholder="opensandbox" />
        </Form.Item>
      </Form>
    </Modal>
  )
}
