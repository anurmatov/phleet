import { useEffect, useSyncExternalStore } from 'react'
import { ChatActivityStore, SEEN_KEY, type ChatActivity } from '../chatActivity'
import { apiFetch } from '../utils'

let store: ChatActivityStore | null = null

function storage(): Storage | null {
  try { return window.localStorage } catch { return null }
}

/** The page's one record of which chat threads moved on. Made on first use. */
export function chatActivity(): ChatActivityStore {
  store ??= new ChatActivityStore({ fetch: apiFetch, storage: storage() })
  return store
}

/** Which threads have moved on, re-rendering the caller when that changes. */
export function useChatActivity(): ChatActivity {
  const activity = chatActivity()
  return useSyncExternalStore(activity.subscribe, activity.snapshot)
}

/** How often the list of threads is read again while the page is in front. */
const REFRESH_MS = 60_000

/**
 * Keeps the record fresh while the page is open: read now, again every minute while the page is
 * visible, and when it comes back to the front. Also follows another tab marking a thread read.
 * Mounted once, by the app.
 */
export function useChatActivityRefresh(): void {
  useEffect(() => {
    const activity = chatActivity()
    const refresh = () => {
      // A deployment without chat is asked once, not every minute.
      if (document.visibilityState === 'visible' && activity.snapshot().available !== false) void activity.refresh()
    }
    void activity.refresh()
    const timer = window.setInterval(refresh, REFRESH_MS)
    const onStorage = (event: StorageEvent) => { if (event.key === SEEN_KEY) activity.reloadSeen() }
    document.addEventListener('visibilitychange', refresh)
    window.addEventListener('storage', onStorage)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', refresh)
      window.removeEventListener('storage', onStorage)
    }
  }, [])
}
