import { useEffect, useRef, useState } from 'react'
import type { Metrics } from '@/api/types'

export interface MetricsEvent {
  type: 'connected' | 'metrics' | 'error'
  data?: Metrics
  error?: string
}

export function useMetricsStream(
  sandboxId: string,
  onEvent: (event: MetricsEvent) => void,
  enabled = true,
) {
  const [status, setStatus] = useState<'idle' | 'connecting' | 'connected' | 'error'>('idle')
  const onEventRef = useRef(onEvent)
  onEventRef.current = onEvent

  useEffect(() => {
    if (!enabled || !sandboxId) return
    const controller = new AbortController()
    setStatus('connecting')

    ;(async () => {
      try {
        const response = await fetch(`/api/v1/sandboxes/${sandboxId}/metrics/watch`, {
          signal: controller.signal,
        })
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`)
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        setStatus('connected')
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })
          const parts = buffer.split('\n\n')
          buffer = parts.pop() ?? ''
          for (const part of parts) {
            const line = part.split('\n').find((l) => l.startsWith('data: '))
            if (!line) continue
            onEventRef.current(JSON.parse(line.slice(6)))
          }
        }
      } catch (err) {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setStatus('error')
          onEventRef.current({
            type: 'error',
            error: err instanceof Error ? err.message : 'stream failed',
          })
        }
      }
    })()

    return () => controller.abort()
  }, [sandboxId, enabled])

  return status
}
