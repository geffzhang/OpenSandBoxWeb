import { useEffect, useRef } from 'react'
import * as echarts from 'echarts'
import type { MetricsPoint } from '@/api/types'

export default function MetricsChart({ data }: { data: MetricsPoint[] }) {
  const containerRef = useRef<HTMLDivElement>(null)
  const chartRef = useRef<echarts.ECharts | null>(null)

  useEffect(() => {
    if (!containerRef.current) return
    chartRef.current ??= echarts.init(containerRef.current)
    const chart = chartRef.current
    chart.setOption({
      tooltip: { trigger: 'axis' },
      legend: { data: ['CPU %', '内存 MiB'] },
      xAxis: { type: 'time' },
      yAxis: [
        { type: 'value', name: 'CPU %', max: 100 },
        { type: 'value', name: '内存 MiB' },
      ],
      series: [
        {
          name: 'CPU %',
          type: 'line',
          showSymbol: false,
          data: data.map((point) => [point.timestamp, point.cpu]),
        },
        {
          name: '内存 MiB',
          type: 'line',
          showSymbol: false,
          yAxisIndex: 1,
          data: data.map((point) => [point.timestamp, point.memory]),
        },
      ],
    })
  }, [data])

  useEffect(
    () => () => {
      chartRef.current?.dispose()
      chartRef.current = null
    },
    [],
  )

  return <div ref={containerRef} style={{ height: 320 }} />
}
