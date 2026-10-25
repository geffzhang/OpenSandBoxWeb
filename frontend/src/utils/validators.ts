export function validateImageUri(uri: string): string | null {
  if (!uri.trim()) return '镜像地址不能为空'
  if (!/^[a-z0-9][a-z0-9._/-]*(:[a-zA-Z0-9._-]+)?(@sha256:[a-f0-9]{64})?$/i.test(uri.trim())) {
    return '镜像地址格式无效（如 ubuntu:24.04 或 registry.example.com/repo/img:tag）'
  }
  return null
}

export function validateCpu(cpu: string): string | null {
  if (!cpu.trim()) return null
  if (!/^\d+(\.\d+)?m?$/.test(cpu.trim())) return 'CPU 配额格式无效（如 500m 或 1.5）'
  return null
}

export function validateMemory(memory: string): string | null {
  if (!memory.trim()) return null
  if (!/^\d+(\.\d+)?(Ki|Mi|Gi|Ti)?$/i.test(memory.trim())) {
    return '内存配额格式无效（如 512Mi 或 2Gi）'
  }
  return null
}

export function validateTimeout(timeout: number | undefined): string | null {
  if (timeout !== undefined && (Number.isNaN(timeout) || timeout <= 0)) {
    return '超时时间必须大于 0'
  }
  return null
}
