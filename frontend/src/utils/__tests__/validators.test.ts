import { describe, expect, it } from 'vitest'
import { validateCpu, validateImageUri, validateMemory, validateTimeout } from '../validators'

describe('validateImageUri', () => {
  it('accepts a plain image tag', () => {
    expect(validateImageUri('ubuntu:24.04')).toBeNull()
  })

  it('accepts a registry path with digest', () => {
    expect(
      validateImageUri('registry.example.com/team/app@sha256:' + 'a'.repeat(64)),
    ).toBeNull()
  })

  it('rejects an empty value', () => {
    expect(validateImageUri('  ')).toBe('镜像地址不能为空')
  })

  it('rejects spaces in the name', () => {
    expect(validateImageUri('bad image')).toContain('格式无效')
  })
})

describe('validateCpu', () => {
  it('accepts millicore and plain numbers', () => {
    expect(validateCpu('500m')).toBeNull()
    expect(validateCpu('1.5')).toBeNull()
  })

  it('accepts empty as unset', () => {
    expect(validateCpu('')).toBeNull()
  })

  it('rejects garbage', () => {
    expect(validateCpu('abc')).toContain('格式无效')
  })
})

describe('validateMemory', () => {
  it('accepts Mi/Gi suffixes', () => {
    expect(validateMemory('512Mi')).toBeNull()
    expect(validateMemory('2Gi')).toBeNull()
  })

  it('rejects garbage', () => {
    expect(validateMemory('lots of ram')).toContain('格式无效')
  })
})

describe('validateTimeout', () => {
  it('accepts positive numbers and undefined', () => {
    expect(validateTimeout(300)).toBeNull()
    expect(validateTimeout(undefined)).toBeNull()
  })

  it('rejects zero and negatives', () => {
    expect(validateTimeout(0)).toContain('大于 0')
  })
})
