import { describe, expect, it } from 'vitest'
import { render } from '@testing-library/react'
import StatusBadge from '../StatusBadge'

// antd renders preset colors (e.g. "green") as a class with no inline
// background, while non-preset values (e.g. "#bfbfbf") get an inline
// background. jsdom may normalize inline background to rgb(...).
function normalizeColor(color: string): string {
  const hex = /^#([0-9a-f]{6})$/i.exec(color)
  if (hex) {
    const value = Number.parseInt(hex[1], 16)
    return `rgb(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255})`
  }
  return color.toLowerCase()
}

function dotColor(container: HTMLElement): string {
  const dot = container.querySelector('.ant-badge-status-dot')
  expect(dot).not.toBeNull()
  const element = dot as HTMLElement
  if (element.style.background) {
    return normalizeColor(element.style.background)
  }
  const preset = [...element.classList].find(
    (name) => name.startsWith('ant-badge-color-') || name.startsWith('ant-badge-status-color-'),
  )
  return preset?.replace(/^ant-badge-(?:status-)?color-/, '') ?? ''
}

describe('StatusBadge', () => {
  it('renders a running sandbox with a green dot', () => {
    const { container } = render(<StatusBadge state="Running" />)

    expect(container).toHaveTextContent('Running')
    expect(dotColor(container)).toBe(normalizeColor('green'))
  })

  it('renders a deleted sandbox with a gray dot', () => {
    const { container } = render(<StatusBadge state="Deleted" />)

    expect(dotColor(container)).toBe(normalizeColor('#bfbfbf'))
  })

  it('falls back to a gray dot for unknown states and shows the state text', () => {
    const { container } = render(<StatusBadge state="UnknownState" />)

    expect(dotColor(container)).toBe(normalizeColor('#bfbfbf'))
    expect(container).toHaveTextContent('UnknownState')
  })
})
