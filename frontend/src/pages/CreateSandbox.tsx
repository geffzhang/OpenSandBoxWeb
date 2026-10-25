import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  App,
  Button,
  Card,
  Col,
  Form,
  Input,
  InputNumber,
  Radio,
  Row,
  Select,
  Space,
} from 'antd'
import { MinusCircleOutlined, PlusOutlined } from '@ant-design/icons'
import { sandboxApi } from '@/api/sandbox'
import { extractErrorMessage } from '@/api/client'
import type { CreateSandboxRequest, NetworkRule, VolumeSpec } from '@/api/types'
import { validateCpu, validateImageUri, validateMemory, validateTimeout } from '@/utils/validators'

interface CreateFormValues {
  imageUri: string
  imageUser?: string
  imagePassword?: string
  name?: string
  entrypoint?: string[]
  cpu?: string
  memory?: string
  gpu?: string
  timeout?: number
  env?: { key: string; value: string }[]
  defaultAction?: 'allow' | 'deny'
  egress?: { action: 'allow' | 'deny'; target: string }[]
  volumes?: { name: string; mountPath: string; hostPath?: string }[]
}

export default function CreateSandbox() {
  const navigate = useNavigate()
  const { message } = App.useApp()
  const [form] = Form.useForm<CreateFormValues>()
  const [submitting, setSubmitting] = useState(false)

  const onFinish = async (values: CreateFormValues) => {
    const resourceLimits = Object.fromEntries(
      Object.entries({ cpu: values.cpu ?? '', memory: values.memory ?? '', gpu: values.gpu ?? '' })
        .filter(([, value]) => value.trim()),
    )
    const env = (values.env ?? [])
      .filter((kv) => (kv.key ?? '').trim())
      .reduce<Record<string, string>>((acc, kv) => {
        acc[(kv.key ?? '').trim()] = kv.value ?? ''
        return acc
      }, {})
    const request: CreateSandboxRequest = {
      image: {
        uri: values.imageUri.trim(),
        ...(values.imageUser?.trim() || values.imagePassword
          ? {
              auth: {
                username: values.imageUser?.trim() ?? '',
                password: values.imagePassword ?? '',
              },
            }
          : {}),
      },
      entrypoint: values.entrypoint ?? [],
      resourceLimits,
      timeout: values.timeout ?? null,
      env: Object.keys(env).length ? env : undefined,
      metadata: values.name?.trim() ? { name: values.name.trim() } : undefined,
      networkPolicy:
        values.defaultAction || (values.egress ?? []).length
          ? {
              defaultAction: values.defaultAction,
              egress: (values.egress ?? [])
                .filter((rule) => (rule.target ?? '').trim())
                .map<NetworkRule>((rule) => ({
                  action: rule.action,
                  target: (rule.target ?? '').trim(),
                })),
            }
          : undefined,
      volumes: (values.volumes ?? [])
        .filter((v) => (v.name ?? '').trim() && (v.mountPath ?? '').trim())
        .map<VolumeSpec>((v) => ({
          name: (v.name ?? '').trim(),
          mountPath: (v.mountPath ?? '').trim(),
          hostPath: v.hostPath?.trim() || undefined,
        })),
    }

    setSubmitting(true)
    try {
      const created = await sandboxApi.create(request)
      message.success(`沙箱 ${created.id} 创建成功`)
      navigate(`/sandboxes/${created.id}`)
    } catch (error) {
      message.error(extractErrorMessage(error))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card title="创建沙箱" style={{ maxWidth: 900 }}>
      <Form
        form={form}
        layout="vertical"
        onFinish={onFinish}
        initialValues={{ entrypoint: [], env: [], egress: [], volumes: [], defaultAction: 'allow' }}
      >
        <Row gutter={16}>
          <Col span={12}>
            <Form.Item
              name="imageUri"
              label="镜像地址"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateImageUri(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 ubuntu:24.04 或 registry.example.com/repo/img:tag" />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="name" label="名称（写入 metadata.name）">
              <Input placeholder="可选" />
            </Form.Item>
          </Col>
        </Row>

        <Form.Item name="entrypoint" label="入口命令（可多个，按顺序执行）">
          <Select mode="tags" placeholder="如 /bin/sh" open={false} />
        </Form.Item>

        <Row gutter={16}>
          <Col span={8}>
            <Form.Item
              name="cpu"
              label="CPU 配额"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateCpu(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 500m" />
            </Form.Item>
          </Col>
          <Col span={8}>
            <Form.Item
              name="memory"
              label="内存配额"
              rules={[
                {
                  validator: (_, value: string) => {
                    const error = validateMemory(value ?? '')
                    return error ? Promise.reject(new Error(error)) : Promise.resolve()
                  },
                },
              ]}
            >
              <Input placeholder="如 512Mi" />
            </Form.Item>
          </Col>
          <Col span={8}>
            <Form.Item name="gpu" label="GPU 数量">
              <Input placeholder="如 1" />
            </Form.Item>
          </Col>
        </Row>

        <Form.Item
          name="timeout"
          label="超时时间（秒）"
          rules={[
            {
              validator: (_, value: number | undefined) => {
                const error = validateTimeout(value ?? undefined)
                return error ? Promise.reject(new Error(error)) : Promise.resolve()
              },
            },
          ]}
        >
          <InputNumber min={1} style={{ width: 200 }} placeholder="可选" />
        </Form.Item>

        <Form.Item label="镜像仓库认证">
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item name="imageUser" noStyle>
                <Input placeholder="用户名（可选）" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item name="imagePassword" noStyle>
                <Input.Password placeholder="密码（可选）" />
              </Form.Item>
            </Col>
          </Row>
        </Form.Item>

        <Form.List name="env">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'key']}>
                    <Input placeholder="环境变量名" />
                  </Form.Item>
                  <Form.Item name={[name, 'value']}>
                    <Input placeholder="值" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add()}>
                添加环境变量
              </Button>
            </>
          )}
        </Form.List>

        <Form.Item name="defaultAction" label="网络策略默认动作">
          <Radio.Group>
            <Radio value="allow">允许</Radio>
            <Radio value="deny">拒绝</Radio>
          </Radio.Group>
        </Form.Item>

        <Form.List name="egress">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'action']}>
                    <Select
                      style={{ width: 120 }}
                      options={[
                        { value: 'allow', label: 'allow' },
                        { value: 'deny', label: 'deny' },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item name={[name, 'target']}>
                    <Input placeholder="目标 CIDR，如 10.0.0.0/8" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({ action: 'allow' })}>
                添加出站规则
              </Button>
            </>
          )}
        </Form.List>

        <Form.List name="volumes">
          {(fields, { add, remove }) => (
            <>
              {fields.map(({ key, name }) => (
                <Space key={key} align="baseline">
                  <Form.Item name={[name, 'name']}>
                    <Input placeholder="卷名" />
                  </Form.Item>
                  <Form.Item name={[name, 'mountPath']}>
                    <Input placeholder="挂载路径，如 /data" />
                  </Form.Item>
                  <Form.Item name={[name, 'hostPath']}>
                    <Input placeholder="主机路径（可选）" />
                  </Form.Item>
                  <MinusCircleOutlined onClick={() => remove(name)} />
                </Space>
              ))}
              <Button type="dashed" icon={<PlusOutlined />} onClick={() => add()}>
                添加卷
              </Button>
            </>
          )}
        </Form.List>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={submitting}>
            创建沙箱
          </Button>
        </Form.Item>
      </Form>
    </Card>
  )
}
