import { Link, useParams } from 'react-router-dom'
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Result,
  Space,
  Spin,
  Tag,
  Timeline,
  Typography,
} from 'antd'
import { ArrowLeftOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { extractErrorMessage } from '@/api/client'
import { volumeApi } from '@/api/volume'
import { formatTime } from '@/utils/format'
import {
  CLASSIFICATION_COLORS,
  LIFECYCLE_COLORS,
  classificationLabel,
  lifecycleLabel,
} from '@/utils/volumeLabels'

export default function VolumeDetail() {
  const { namespace = '', claimName = '' } = useParams()
  const query = useQuery({
    queryKey: ['volumes', 'detail', namespace, claimName],
    queryFn: () => volumeApi.get(namespace, claimName),
    retry: false,
  })

  if (query.isLoading) return <Spin />
  if (query.isError) {
    return (
      <Result
        status="404"
        title="卷不存在或无权访问"
        subTitle={extractErrorMessage(query.error)}
        extra={
          <Link to="/volumes">
            <Button icon={<ArrowLeftOutlined />}>返回卷列表</Button>
          </Link>
        }
      />
    )
  }

  const detail = query.data!
  const volume = detail.volume

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space>
        <Link to="/volumes">
          <Button icon={<ArrowLeftOutlined />}>返回</Button>
        </Link>
        <Typography.Title level={4} style={{ margin: 0 }}>
          {volume.namespace}/{volume.claimName}
        </Typography.Title>
        <Tag color={CLASSIFICATION_COLORS[volume.classification] ?? 'default'}>
          {classificationLabel(volume.classification)}
        </Tag>
        {volume.lifecycle && (
          <Tag color={LIFECYCLE_COLORS[volume.lifecycle] ?? 'default'}>
            {lifecycleLabel(volume.lifecycle)}
          </Tag>
        )}
      </Space>

      {volume.violations.length > 0 && (
        <Alert
          type="warning"
          showIcon
          message="违规"
          description={volume.violations.join('、')}
        />
      )}

      <Card title="台账">
        <Descriptions column={2} size="small">
          <Descriptions.Item label="租户">
            {volume.tenantName ?? volume.tenant ?? '-'}
          </Descriptions.Item>
          <Descriptions.Item label="Owner">
            {volume.ownerKind ? `${volume.ownerKind}/${volume.ownerId ?? '-'}` : '-'}
          </Descriptions.Item>
          <Descriptions.Item label="角色">{volume.role ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="挂载路径">{volume.mountPath ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="状态">
            {volume.state ?? '-'}
            {volume.stateReason ? ` (${volume.stateReason})` : ''}
          </Descriptions.Item>
          <Descriptions.Item label="标签状态">{volume.labelState ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="登记 UID">{volume.registeredUid ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="观测 UID">{volume.observedUid ?? '-'}</Descriptions.Item>
          <Descriptions.Item label="登记时间">
            {formatTime(volume.registeredAt)}
          </Descriptions.Item>
          <Descriptions.Item label="最后观测">
            {formatTime(volume.lastSeenAt)}
          </Descriptions.Item>
        </Descriptions>
      </Card>

      <Card title="集群观测">
        {detail.observed ? (
          <Descriptions column={2} size="small">
            <Descriptions.Item label="Phase">{detail.observed.phase}</Descriptions.Item>
            <Descriptions.Item label="StorageClass">
              {detail.observed.storageClass ?? '-'}
            </Descriptions.Item>
            <Descriptions.Item label="容量">
              {detail.observed.capacity ?? detail.observed.requestedStorage ?? '-'}
            </Descriptions.Item>
            <Descriptions.Item label="挂载">
              {detail.observed.mounted ? '是' : '否'}
            </Descriptions.Item>
            <Descriptions.Item label="挂载 Pod" span={2}>
              {detail.observed.mountedBy.length > 0 ? (
                <Space wrap>
                  {detail.observed.mountedBy.map((pod) => (
                    <Tag key={pod}>{pod}</Tag>
                  ))}
                </Space>
              ) : (
                '-'
              )}
            </Descriptions.Item>
            <Descriptions.Item label="K8s 创建时间">
              {formatTime(detail.observed.createdAt)}
            </Descriptions.Item>
            <Descriptions.Item label="删除时间戳">
              {formatTime(detail.observed.deletionTimestamp)}
            </Descriptions.Item>
            <Descriptions.Item label="原始标签" span={2}>
              <Space wrap>
                {Object.entries(detail.observed.labels).map(([key, value]) => (
                  <Tag key={key}>
                    {key}={value}
                  </Tag>
                ))}
              </Space>
            </Descriptions.Item>
          </Descriptions>
        ) : (
          <Typography.Text type="secondary">
            当前未观测到该 PVC（可能已被删除或命名空间扫描失败）。
          </Typography.Text>
        )}
      </Card>

      <Card title="事件">
        {detail.events.length === 0 ? (
          <Typography.Text type="secondary">暂无事件</Typography.Text>
        ) : (
          <Timeline
            items={detail.events.map((event) => ({
              color: event.type.includes('unexpected') ? 'red' : 'blue',
              children: (
                <Space direction="vertical" size={0}>
                  <Typography.Text strong>{event.type}</Typography.Text>
                  <Typography.Text type="secondary">
                    {formatTime(event.createdAt)} · {event.actor}
                  </Typography.Text>
                  {event.reason && <Typography.Text>{event.reason}</Typography.Text>}
                </Space>
              ),
            }))}
          />
        )}
      </Card>
    </Space>
  )
}
