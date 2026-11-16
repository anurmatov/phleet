import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch } from '../utils'
import { parseUsageSummary, usageFailure, usageQuery } from '../usage'
import type { UsageFailure, UsageFilters, UsageSummary } from '../usage'

export interface UsageState {
  /** A request is in flight. */
  loading: boolean
  /** The last answer that arrived, kept while a later request fails so it can be shown as stale. */
  data: UsageSummary | null
  /** When {@link data} was fetched. */
  fetchedAt: Date | null
  /** Why the latest request has no answer, or null when it succeeded or is still running. */
  failure: UsageFailure | null
}

/**
 * `/api/usage` for the current filters: once on mount, once per change, and on {@link reload}.
 * Nothing polls. A newer request aborts the one before it, and an answer that is no longer the
 * latest is discarded even if it arrives, so an older filter never replaces a newer one.
 */
export function useUsage(filters: UsageFilters): { state: UsageState; reload: () => void } {
  const [state, setState] = useState<UsageState>({ loading: true, data: null, fetchedAt: null, failure: null })
  const latest = useRef(0)
  const controller = useRef<AbortController | null>(null)
  // The filters of the request in flight, so a repeated effect for the same filters (StrictMode's
  // second mount) does not send it twice. Reload bypasses it on purpose.
  const inFlight = useRef<string | null>(null)

  const load = useCallback((current: UsageFilters) => {
    const id = ++latest.current
    controller.current?.abort()
    const abort = new AbortController()
    controller.current = abort
    inFlight.current = JSON.stringify(current)
    setState(s => ({ ...s, loading: true, failure: null }))

    const finish = (next: Partial<UsageState>) => {
      if (id !== latest.current) return
      inFlight.current = null
      setState(s => ({ ...s, ...next, loading: false }))
    }

    apiFetch(`/api/usage?${usageQuery(current, new Date())}`, { signal: abort.signal })
      .then(async response => {
        const body: unknown = await response.json().catch(() => null)
        if (!response.ok) return finish({ failure: usageFailure(response.status, body) })
        const summary = parseUsageSummary(body)
        if (summary === null) return finish({ failure: 'unavailable' })
        finish({ data: summary, fetchedAt: new Date(), failure: null })
      })
      .catch(() => {
        if (abort.signal.aborted) return
        finish({ failure: 'unavailable' })
      })
  }, [])

  const { preset, metric, agent, provider, model } = filters
  useEffect(() => {
    const next = { preset, metric, agent, provider, model }
    if (inFlight.current === JSON.stringify(next)) return
    load(next)
  }, [load, preset, metric, agent, provider, model])

  const reload = useCallback(() => load(filters), [load, filters])
  return { state, reload }
}
