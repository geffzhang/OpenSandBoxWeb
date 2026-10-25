import { Badge } from 'antd'

const COLORS: Record<string, string> = {
  Running: 'green',
  Paused: 'orange',
  Pending: 'blue',
  Pausing: 'cyan',
  Stopping: 'gold',
  Terminated: 'default',
  Failed: 'red',
}

export default function StatusBadge({ state }: { state: string }) {
  return <Badge color={COLORS[state] ?? 'default'} text={state} />
}
