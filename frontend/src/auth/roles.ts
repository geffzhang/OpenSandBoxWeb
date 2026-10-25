export function hasConsoleRole(roles: readonly string[], required: string): boolean {
  if (roles.includes(required)) return true
  if (required === 'sandbox:viewer') return roles.includes('sandbox:admin')
  if (required === 'volume:viewer') return roles.includes('volume:admin')
  return false
}
