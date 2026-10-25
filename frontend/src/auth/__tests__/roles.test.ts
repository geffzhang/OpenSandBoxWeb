import { describe, expect, it } from 'vitest'
import { hasConsoleRole } from '../roles'

describe('hasConsoleRole', () => {
  it('lets admin satisfy viewer in the same facet', () => {
    expect(hasConsoleRole(['sandbox:admin'], 'sandbox:viewer')).toBe(true)
    expect(hasConsoleRole(['volume:admin'], 'volume:viewer')).toBe(true)
  })

  it('does not let viewer satisfy admin', () => {
    expect(hasConsoleRole(['sandbox:viewer'], 'sandbox:admin')).toBe(false)
    expect(hasConsoleRole(['volume:viewer'], 'volume:admin')).toBe(false)
  })

  it('keeps facets independent', () => {
    expect(hasConsoleRole(['sandbox:admin'], 'volume:viewer')).toBe(false)
    expect(hasConsoleRole(['volume:admin'], 'sandbox:viewer')).toBe(false)
  })

  it('matches exact roles and denies empty sets', () => {
    expect(hasConsoleRole(['sandbox:viewer'], 'sandbox:viewer')).toBe(true)
    expect(hasConsoleRole([], 'sandbox:viewer')).toBe(false)
  })
})
