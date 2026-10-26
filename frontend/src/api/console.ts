import { apiClient } from './client'

export type ConsoleMe = {
  subject: string
  username: string
  roles: string[]
}

export const consoleApi = {
  async me(): Promise<ConsoleMe> {
    const response = await apiClient.get<ConsoleMe>('/console/me')
    return response.data
  },
}
