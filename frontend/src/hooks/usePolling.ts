import { useQuery, type UseQueryOptions } from '@tanstack/react-query'

export function usePolling<T>(
  key: string[],
  fetcher: () => Promise<T>,
  intervalMs: number,
  enabled = true,
  options?: Omit<UseQueryOptions<T>, 'queryKey' | 'queryFn'>,
) {
  return useQuery<T>({
    queryKey: key,
    queryFn: fetcher,
    refetchInterval: intervalMs,
    enabled,
    ...options,
  })
}
