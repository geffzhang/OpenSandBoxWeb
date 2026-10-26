import type { VolumeImportItem } from '@/api/types'

export interface VolumeCsvParseResult {
  items: VolumeImportItem[]
  errors: string[]
}

type Column = keyof VolumeImportItem

const HEADER_ALIASES: Record<string, Column> = {
  tenant: 'tenant',
  namespace: 'namespace',
  claimname: 'claimName',
  ownerkind: 'ownerKind',
  ownerid: 'ownerId',
  role: 'role',
  mountpath: 'mountPath',
  lifecycle: 'lifecycle',
  sandboxid: 'sandboxId',
}

const REQUIRED: Column[] = [
  'tenant',
  'namespace',
  'claimName',
  'ownerKind',
  'ownerId',
  'lifecycle',
]

const normalizeHeader = (value: string) =>
  value.trim().replace(/[\s_-]/g, '').toLowerCase()

function parseRecords(text: string): string[][] {
  const source = text.replace(/^﻿/, '')
  const records: string[][] = []
  let record: string[] = []
  let field = ''
  let quoted = false

  for (let index = 0; index < source.length; index += 1) {
    const char = source[index]
    if (quoted) {
      if (char === '"') {
        if (source[index + 1] === '"') {
          field += '"'
          index += 1
        } else {
          quoted = false
        }
      } else {
        field += char
      }
      continue
    }

    if (char === '"') {
      quoted = true
    } else if (char === ',') {
      record.push(field)
      field = ''
    } else if (char === '\r') {
      continue
    } else if (char === '\n') {
      record.push(field)
      records.push(record)
      record = []
      field = ''
    } else {
      field += char
    }
  }

  if (quoted) throw new Error('CSV 含未闭合的引号')
  if (field.length > 0 || record.length > 0) {
    record.push(field)
    records.push(record)
  }

  return records.filter(
    (values) => values.length > 1 || values[0]?.trim().length > 0,
  )
}

export function parseVolumeCsv(text: string): VolumeCsvParseResult {
  if (!text.trim()) return { items: [], errors: ['CSV 为空'] }

  let records: string[][]
  try {
    records = parseRecords(text)
  } catch (error) {
    return {
      items: [],
      errors: [error instanceof Error ? error.message : 'CSV 解析失败'],
    }
  }

  if (records.length === 0) return { items: [], errors: ['CSV 为空'] }

  const columns = records[0].map((header) => HEADER_ALIASES[normalizeHeader(header)])
  const missing = REQUIRED.filter((column) => !columns.includes(column))
  if (missing.length > 0) {
    return { items: [], errors: [`缺少表头: ${missing.join(', ')}`] }
  }

  const items: VolumeImportItem[] = []
  const errors: string[] = []

  records.slice(1).forEach((values, rowIndex) => {
    const line = rowIndex + 2
    if (values.length !== records[0].length) {
      errors.push(`第 ${line} 行: 列数与表头不一致`)
      return
    }

    const row: Partial<Record<Column, string>> = {}
    columns.forEach((column, columnIndex) => {
      if (column) row[column] = values[columnIndex].trim()
    })

    const absent = REQUIRED.filter((column) => !row[column])
    if (absent.length > 0) {
      errors.push(`第 ${line} 行: 缺少字段 ${absent.join(', ')}`)
      return
    }

    if (row.lifecycle !== 'business' && row.lifecycle !== 'ephemeral') {
      errors.push(`第 ${line} 行: lifecycle 必须是 business 或 ephemeral`)
      return
    }

    items.push({
      tenant: row.tenant!,
      namespace: row.namespace!,
      claimName: row.claimName!,
      ownerKind: row.ownerKind!,
      ownerId: row.ownerId!,
      role: row.role || undefined,
      mountPath: row.mountPath || undefined,
      lifecycle: row.lifecycle!,
      sandboxId: row.sandboxId || undefined,
    })
  })

  return { items, errors }
}
