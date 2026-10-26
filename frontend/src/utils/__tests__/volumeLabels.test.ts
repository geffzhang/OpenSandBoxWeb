import { describe, expect, it } from 'vitest'
import { classificationLabel, lifecycleLabel } from '../volumeLabels'

describe('volumeLabels', () => {
  it('maps known classifications', () => {
    expect(classificationLabel('unregistered')).toBe('未登记')
    expect(classificationLabel('missing')).toBe('缺失')
  })

  it('falls back to the raw value', () => {
    expect(classificationLabel('future-state')).toBe('future-state')
    expect(lifecycleLabel('other')).toBe('other')
  })
})
