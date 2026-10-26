import { useRef, useState } from 'react'
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
  reactivated: 'purple',
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
  reactivated: '重新激活',
  'label-pending': '标签待重试',
  conflict: '冲突',
  invalid: '无效',
  'not-found': 'PVC 不存在',
  'would-register': '将登记',
  'would-update': '将更新',
}

interface ImportSnapshot {
  text: string
  items: VolumeImportItem[]
}

interface ImportOutcome extends ImportSnapshot {
  response: VolumeImportResponse
}

export default function VolumeImport() {
  const { message } = App.useApp()
  const [csvText, setCsvText] = useState('')
  const [parsed, setParsed] = useState<VolumeCsvParseResult | null>(null)
  const [dryRun, setDryRun] = useState<ImportOutcome | null>(null)
  const [writeOutcome, setWriteOutcome] = useState<ImportOutcome | null>(null)
  const csvTextRef = useRef('')

  const reset = () => {
    setParsed(null)
    setDryRun(null)
    setWriteOutcome(null)
  }

  const updateCsvText = (value: string) => {
    csvTextRef.current = value
    setCsvText(value)
    reset()
  }

  const isCurrentText = (text: string) => text === csvTextRef.current

  const dryRunMutation = useMutation({
    mutationFn: (snapshot: ImportSnapshot) =>
      volumeApi.import({ dryRun: true, items: snapshot.items }),
    onSuccess: (response, snapshot) => {
      // 只有文本未变化的 dry-run 结果才可用于授权写入
      if (!isCurrentText(snapshot.text)) return
      setDryRun({ ...snapshot, response })
      setWriteOutcome(null)
    },
    onError: (error, snapshot) => {
      if (!isCurrentText(snapshot.text)) return
      // 失败的 dry run 不能授权写入；清空旧的 dry-run 结果
      setDryRun(null)
      message.error(extractErrorMessage(error))
    },
  })

  const writeMutation = useMutation({
    mutationFn: (snapshot: ImportSnapshot) =>
      volumeApi.import({ dryRun: false, items: snapshot.items }),
    onSuccess: (response, snapshot) => {
      // 写入期间文本已变化时忽略迟到的结果，避免渲染到新预览下
      if (!isCurrentText(snapshot.text)) return
      // 写入完成后必须重新 dry run 才能再次写入
      setDryRun(null)
      setWriteOutcome({ ...snapshot, response })
      message.success('导入完成')
    },
    onError: (error, snapshot) => {
      if (!isCurrentText(snapshot.text)) return
      message.error(extractErrorMessage(error))
    },
  })

  const uploadProps: UploadProps = {
    accept: '.csv,text/csv',
    showUploadList: false,
    beforeUpload: async (file) => {
      updateCsvText(await file.text())
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
    dryRun !== null &&
    dryRun.text === csvText &&
    dryRun.response.dryRun === true &&
    !dryRunMutation.isPending &&
    !writeMutation.isPending

  const outcome =
    writeOutcome && writeOutcome.text === csvText
      ? writeOutcome
      : dryRun && dryRun.text === csvText
        ? dryRun
        : null

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
          onChange={(event) => updateCsvText(event.target.value)}
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
            onClick={() => {
              if (!parsed) return
              dryRunMutation.mutate({ text: csvText, items: parsed.items })
            }}
          >
            Dry run
          </Button>
          <Button
            danger
            type="primary"
            disabled={!canWrite}
            loading={writeMutation.isPending}
            onClick={() => {
              if (!dryRun) return
              writeMutation.mutate({ text: dryRun.text, items: dryRun.items })
            }}
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
            rowKey={(row, index) => `${index}-${row.namespace}/${row.claimName}`}
            size="small"
            columns={previewColumns}
            dataSource={parsed.items}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 900 }}
          />
        </Card>
      )}

      {outcome && (
        <Card
          title={outcome.response.dryRun ? 'Dry run 结果（未写入）' : '写入结果'}
        >
          <Table<VolumeImportResult>
            rowKey={(row, index) => `${index}-${row.namespace}/${row.claimName}`}
            size="small"
            columns={resultColumns}
            dataSource={outcome.response.results}
            pagination={{ pageSize: 20 }}
            scroll={{ x: 700 }}
          />
        </Card>
      )}
    </Space>
  )
}
