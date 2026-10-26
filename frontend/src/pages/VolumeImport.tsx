import { useState } from 'react'
import { Alert, App, Button, Card, Input, Space, Table, Tag, Typography, Upload } from 'antd'
import type { TableProps, UploadProps } from 'antd'
import { InboxOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { VolumeImportItem, VolumeImportResponse, VolumeImportResult } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { parseVolumeCsv, type VolumeCsvParseResult } from '@/utils/volumeCsv'

const STATUS_COLORS: Record<string, string> = {
  registered: 'green',
  updated: 'blue',
  'label-pending': 'orange',
  conflict: 'red',
  invalid: 'red',
  'not-found': 'magenta',
  'would-register': 'cyan',
  'would-update': 'geekblue',
}

const STATUS_LABELS: Record<string, string> = {
  registered: '已登记',
  updated: '已更新',
  'label-pending': '标签待重试',
  conflict: '冲突',
  invalid: '无效',
  'not-found': 'PVC 不存在',
  'would-register': '将登记',
  'would-update': '将更新',
}

export default function VolumeImport() {
  const { message } = App.useApp()
  const [csvText, setCsvText] = useState('')
  const [parsed, setParsed] = useState<VolumeCsvParseResult | null>(null)
  const [dryRunResult, setDryRunResult] = useState<VolumeImportResponse | null>(null)

  const reset = () => {
    setParsed(null)
    setDryRunResult(null)
  }

  const dryRunMutation = useMutation({
    mutationFn: () =>
      volumeApi.import({ dryRun: true, items: parsed?.items ?? [] }),
    onSuccess: (result) => setDryRunResult(result),
    onError: (error) => {
      // 失败的 dry run 不能授权写入；清空旧的 dry-run 结果
      setDryRunResult(null)
      message.error(extractErrorMessage(error))
    },
  })

  const writeMutation = useMutation({
    mutationFn: () =>
      volumeApi.import({ dryRun: false, items: parsed?.items ?? [] }),
    onSuccess: (result) => {
      setDryRunResult(result)
      message.success('导入完成')
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const uploadProps: UploadProps = {
    accept: '.csv,text/csv',
    showUploadList: false,
    beforeUpload: async (file) => {
      setCsvText(await file.text())
      reset()
      return false
    },
  }

  const previewColumns: TableProps<VolumeImportItem>['columns'] = [
    { title: '租户', dataIndex: 'tenant', key: 'tenant', width: 110 },
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 140 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 180 },
    {
      title: 'Owner',
      key: 'owner',
      render: (_, record) => `${record.ownerKind}/${record.ownerId}`,
    },
    { title: 'Lifecycle', dataIndex: 'lifecycle', key: 'lifecycle', width: 110 },
    { title: '挂载路径', dataIndex: 'mountPath', key: 'mountPath' },
  ]

  const resultColumns: TableProps<VolumeImportResult>['columns'] = [
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 150 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 200 },
    {
      title: '结果',
      dataIndex: 'status',
      key: 'status',
      width: 130,
      render: (value: string) => (
        <Tag color={STATUS_COLORS[value] ?? 'default'}>{STATUS_LABELS[value] ?? value}</Tag>
      ),
    },
    { title: '说明', dataIndex: 'message', key: 'message', render: (value?: string) => value ?? '-' },
  ]

  const canWrite =
    parsed !== null &&
    parsed.items.length > 0 &&
    dryRunResult?.dryRun === true &&
    !writeMutation.isPending

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card title="CSV 回填导入">
        <Alert
          type="info"
          showIcon
          message="流程：粘贴或上传 CSV → 解析预览 → Dry run → 确认写入"
          description="表头固定为 tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId；导入不会覆盖其他租户的归属。"
          style={{ marginBottom: 16 }}
        />

        <Upload.Dragger {...uploadProps} style={{ marginBottom: 16 }}>
          <p className="ant-upload-drag-icon">
            <InboxOutlined />
          </p>
          <p className="ant-upload-text">点击或拖拽 CSV 到此处</p>
          <p className="ant-upload-hint">也可以直接粘贴到下方文本框</p>
        </Upload.Dragger>

        <Input.TextArea
          value={csvText}
          onChange={(event) => {
            setCsvText(event.target.value)
            reset()
          }}
          rows={8}
          placeholder="粘贴 CSV，第一行为表头"
          style={{ fontFamily: 'monospace' }}
        />

        <Space style={{ marginTop: 16 }}>
          <Button onClick={() => setParsed(parseVolumeCsv(csvText))}>解析预览</Button>
          <Button
            type="primary"
            disabled={!parsed || parsed.items.length === 0}
            loading={dryRunMutation.isPending}
            onClick={() => dryRunMutation.mutate()}
          >
            Dry run
          </Button>
          <Button
            danger
            type="primary"
            disabled={!canWrite}
            loading={writeMutation.isPending}
            onClick={() => writeMutation.mutate()}
          >
            确认写入
          </Button>
          {parsed && (
            <Typography.Text type="secondary">
              有效 {parsed.items.length} 行，错误 {parsed.errors.length} 行
            </Typography.Text>
          )}
        </Space>

        {parsed && parsed.errors.length > 0 && (
          <Alert
            type="error"
            showIcon
            message="CSV 存在错误"
            description={
              <ul style={{ margin: 0, paddingLeft: 20 }}>
                {parsed.errors.map((error) => (
                  <li key={error}>{error}</li>
                ))}
              </ul>
            }
            style={{ marginTop: 16 }}
          />
        )}
      </Card>

      {parsed && parsed.items.length > 0 && (
        <Card title="预览">
          <Table<VolumeImportItem>
            rowKey={(row) => `${row.namespace}/${row.claimName}`}
            size="small"
            columns={previewColumns}
            dataSource={parsed.items}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 900 }}
          />
        </Card>
      )}

      {dryRunResult && (
        <Card
          title={dryRunResult.dryRun ? 'Dry run 结果（未写入）' : '写入结果'}
        >
          <Table<VolumeImportResult>
            rowKey={(row) => `${row.namespace}/${row.claimName}`}
            size="small"
            columns={resultColumns}
            dataSource={dryRunResult.results}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 700 }}
          />
        </Card>
      )}
    </Space>
  )
}
