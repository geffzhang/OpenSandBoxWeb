import { describe, expect, it } from 'vitest'
import { parseVolumeCsv } from '../volumeCsv'

const HEADER =
  'tenant,namespace,claimName,ownerKind,ownerId,role,mountPath,lifecycle,sandboxId'

describe('parseVolumeCsv', () => {
  it('parses quoted fields, a BOM and CRLF', () => {
    const text =
      '﻿' +
      HEADER +
      '\r\n"goodcrew","opensandbox","pvc-1","employee","3f2a","meta","/work, space","business","sb-1"\r\n'

    const result = parseVolumeCsv(text)

    expect(result.errors).toEqual([])
    expect(result.items).toEqual([
      {
        tenant: 'goodcrew',
        namespace: 'opensandbox',
        claimName: 'pvc-1',
        ownerKind: 'employee',
        ownerId: '3f2a',
        role: 'meta',
        mountPath: '/work, space',
        lifecycle: 'business',
        sandboxId: 'sb-1',
      },
    ])
  })

  it('reports a missing required header', () => {
    const result = parseVolumeCsv('tenant,namespace\n"goodcrew","opensandbox"')

    expect(result.items).toEqual([])
    expect(result.errors[0]).toContain('缺少表头')
    expect(result.errors[0]).toContain('claimName')
  })

  it('keeps valid rows and reports invalid lifecycle', () => {
    const text = [
      HEADER,
      'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,/work,business,sb-1',
      'goodcrew,opensandbox,pvc-2,employee,aaaa,meta,/work,forever,',
    ].join('\n')

    const result = parseVolumeCsv(text)

    expect(result.items.map((item) => item.claimName)).toEqual(['pvc-1'])
    expect(result.errors).toEqual(['第 3 行: lifecycle 必须是 business 或 ephemeral'])
  })

  it('handles newlines inside quoted fields', () => {
    const text = [
      HEADER,
      'goodcrew,opensandbox,pvc-1,employee,3f2a,meta,"/line1\nline2",business,',
    ].join('\n')

    const result = parseVolumeCsv(text)

    expect(result.errors).toEqual([])
    expect(result.items[0].mountPath).toBe('/line1\nline2')
  })

  it('reports an unclosed quote', () => {
    const result = parseVolumeCsv(HEADER + '\n"goodcrew,opensandbox')

    expect(result.items).toEqual([])
    expect(result.errors).toEqual(['CSV 含未闭合的引号'])
  })
})
