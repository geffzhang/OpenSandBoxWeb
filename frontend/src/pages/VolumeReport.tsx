import { App, Alert, Button, Card, Col, Empty, Progress, Row, Space, Statistic, Table, Tag, Typography } from 'antd'
import type { TableProps } from 'antd'
import { ScanOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import type { ScanError, VolumeDistributionItem, VolumeViolation } from '@/api/types'
import { volumeApi } from '@/api/volume'
import { useAuth } from '@/auth/AuthContext'
import { formatTime } from '@/utils/format'

export default function VolumeReport() {
  const { can } = useAuth()
  const { message } = App.useApp()
  const queryClient = useQueryClient()

  const reportQuery = useQuery({
    queryKey: ['volumes', 'report'],
    queryFn: volumeApi.report,
  })

  const scanMutation = useMutation({
    mutationFn: volumeApi.scan,
    onSuccess: () => {
      message.success('扫描已完成')
      queryClient.invalidateQueries({ queryKey: ['volumes'] })
    },
    onError: (error) => message.error(extractErrorMessage(error)),
  })

  const report = reportQuery.data
  const counts = report?.counts
  const coverage = report?.coverage

  const distributionColumns: TableProps<VolumeDistributionItem>['columns'] = [
    { title: '名称', dataIndex: 'key', key: 'key' },
    { title: '数量', dataIndex: 'count', key: 'count', width: 100 },
  ]

  const violationColumns: TableProps<VolumeViolation>['columns'] = [
    {
      title: '类型',
      dataIndex: 'type',
      key: 'type',
      width: 180,
      render: (value: string) => <Tag>{value}</Tag>,
    },
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 160 },
    { title: 'PVC', dataIndex: 'claimName', key: 'claimName', width: 200 },
    { title: '说明', dataIndex: 'message', key: 'message' },
  ]

  const errorColumns: TableProps<ScanError>['columns'] = [
    { title: '命名空间', dataIndex: 'namespace', key: 'namespace', width: 200 },
    { title: '错误', dataIndex: 'message', key: 'message' },
  ]

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card
        title="卷治理报告"
        extra={
          can('volume:admin') && (
            <Button
              type="primary"
              icon={<ScanOutlined />}
              loading={scanMutation.isPending}
              onClick={() => scanMutation.mutate()}
            >
              立即扫描
            </Button>
          )
        }
      >
        {reportQuery.isError && (
          <Alert
            type="error"
            showIcon
            message={extractErrorMessage(reportQuery.error)}
            style={{ marginBottom: 16 }}
          />
        )}

        <Row gutter={[16, 16]}>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="观测卷" value={counts?.observed ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="已登记" value={counts?.registered ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="未标注" value={counts?.unlabeled ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="未登记" value={counts?.unregistered ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="漂移" value={counts?.drift ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="缺失" value={counts?.missing ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="新 UID 重现" value={counts?.reappeared ?? 0} />
          </Col>
          <Col xs={12} md={6} lg={3}>
            <Statistic title="失管" value={counts?.unmanaged ?? 0} />
          </Col>
        </Row>

        <Row gutter={[24, 16]} style={{ marginTop: 16 }}>
          <Col xs={24} md={12}>
            <Typography.Text>标签覆盖率</Typography.Text>
            <Progress percent={Number(coverage?.labeledPercent ?? 0)} />
          </Col>
          <Col xs={24} md={12}>
            <Typography.Text>登记覆盖率</Typography.Text>
            <Progress percent={Number(coverage?.registeredPercent ?? 0)} />
          </Col>
        </Row>

        <Space wrap size={24} style={{ marginTop: 16 }}>
          <Typography.Text>
            最后扫描：{formatTime(report?.lastScan?.finishedAt)}
          </Typography.Text>
          <Typography.Text>触发方式：{report?.lastScan?.trigger ?? '-'}</Typography.Text>
          <Typography.Text>
            命名空间：{report?.lastScan?.namespacesSucceeded ?? 0}/
            {report?.lastScan?.namespacesScanned ?? 0}
          </Typography.Text>
        </Space>
      </Card>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card title="按租户分布">
            <Table<VolumeDistributionItem>
              rowKey="key"
              size="small"
              columns={distributionColumns}
              dataSource={report?.byTenant ?? []}
              pagination={false}
            />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title="按命名空间分布">
            <Table<VolumeDistributionItem>
              rowKey="key"
              size="small"
              columns={distributionColumns}
              dataSource={report?.byNamespace ?? []}
              pagination={false}
            />
          </Card>
        </Col>
      </Row>

      <Card
        title={`违规明细${report?.truncated ? '（已截断为前 200 条）' : ''}`}
      >
        {report?.truncated && (
          <Alert
            type="warning"
            showIcon
            message="违规超过 200 条，仅展示前 200 条；请先处理未标注与未登记卷。"
            style={{ marginBottom: 16 }}
          />
        )}
        <Table<VolumeViolation>
          rowKey={(row) => `${row.type}/${row.namespace}/${row.claimName}`}
          size="small"
          columns={violationColumns}
          dataSource={report?.violations ?? []}
          pagination={{ pageSize: 20 }}
          locale={{ emptyText: <Empty description="没有违规" /> }}
        />
      </Card>

      <Card title="上次扫描错误">
        <Table<ScanError>
          rowKey={(row) => `${row.namespace}/${row.message}`}
          size="small"
          columns={errorColumns}
          dataSource={report?.lastScan?.errors ?? []}
          pagination={false}
          locale={{ emptyText: <Empty description="没有扫描错误" /> }}
        />
      </Card>
    </Space>
  )
}
