import { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react'
import { apiFetch } from '../utils'

interface MemoryIdCacheContextValue {
  /** Full UUIDs of all known memories. Empty set on load failure. */
  ids: Set<string>
  /** Refresh the cache (called after edit/delete mutations). */
  refresh: () => void
}

const MemoryIdCacheContext = createContext<MemoryIdCacheContextValue>({
  ids: new Set(),
  refresh: () => {},
})

export function MemoryIdCacheProvider({ children }: { children: ReactNode }) {
  const [ids, setIds] = useState<Set<string>>(new Set())

  const load = useCallback(async (attempt = 0) => {
    try {
      const resp = await apiFetch('/api/memory/ids')
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      const list: string[] = await resp.json()
      setIds(new Set(list))
    } catch {
      if (attempt === 0) {
        // One retry after 5s, then treat cache as empty (per spec)
        setTimeout(() => load(1), 5000)
      } else {
        console.warn('[MemoryIdCache] Failed to load memory IDs — memory links disabled')
      }
    }
  }, [])

  useEffect(() => { load() }, [load])

  return (
    <MemoryIdCacheContext.Provider value={{ ids, refresh: () => load() }}>
      {children}
    </MemoryIdCacheContext.Provider>
  )
}

export function useMemoryIdCache() {
  return useContext(MemoryIdCacheContext)
}
