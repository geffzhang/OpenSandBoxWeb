export const CLASSIFICATION_LABELS: Record<string, string> = {
  registered: '已登记',
  unlabeled: '未标注',
  unregistered: '未登记',
  drift: '标签漂移',
  missing: '缺失',
  reappeared: '新 UID 重现',
  unmanaged: '命名空间失管',
}

export const CLASSIFICATION_COLORS: Record<string, string> = {
  registered: 'green',
  unlabeled: 'red',
  unregistered: 'orange',
  drift: 'volcano',
  missing: 'magenta',
  reappeared: 'purple',
  unmanaged: 'default',
}

export const LIFECYCLE_LABELS: Record<string, string> = {
  business: '业务持久',
  ephemeral: '临时',
}

export const LIFECYCLE_COLORS: Record<string, string> = {
  business: 'blue',
  ephemeral: 'default',
}

export function classificationLabel(value: string): string {
  return CLASSIFICATION_LABELS[value] ?? value
}

export function lifecycleLabel(value: string): string {
  return LIFECYCLE_LABELS[value] ?? value
}
