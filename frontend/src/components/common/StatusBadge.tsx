import { Badge } from 'antd'

const COLORS: Record<string, string> = {
  Creating: 'blue',
  Running: 'green',
  Pausing: 'cyan',
  Paused: 'orange',
  Resuming: 'geekblue',
  Deleting: 'gold',
  Deleted: '#bfbfbf',
  Error: 'red',
}

export default function StatusBadge({ state }: { state: string }) {
  return <Badge color={COLORS[state] ?? '#bfbfbf'} text={state} />
}
