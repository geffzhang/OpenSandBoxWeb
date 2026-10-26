import { useEffect, useRef, useState } from 'react'
import type { Metrics } from '@/api/types'
import { getToken, requireLogin } from '@/auth/auth'

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
        const token = await getToken()
        const response = await fetch(`/api/v1/sandboxes/${sandboxId}/metrics/watch`, {
          signal: controller.signal,
          headers: { Authorization: `Bearer ${token}` },
        })
        if (response.status === 401) {
          await requireLogin()
          return
        }
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`)
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        setStatus('connected')
        try {
          for (;;) {
            const { done, value } = await reader.read()
            if (done) break
            buffer += decoder.decode(value, { stream: true })
            const parts = buffer.split('\n\n')
            buffer = parts.pop() ?? ''
            for (const part of parts) {
              const line = part.split('\n').find((l) => l.startsWith('data: '))
              if (!line) continue
              try {
                onEventRef.current(JSON.parse(line.slice(6)))
              } catch {
                // 跳过格式异常的事件，不中断整个流
                continue
              }
            }
          }
        } catch (err) {
          await reader.cancel()
          throw err
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
