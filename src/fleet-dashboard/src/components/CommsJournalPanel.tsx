import { useCallback, useEffect, useState } from 'react'
import { apiFetch } from '../utils'

interface JournalObserverStatus {
  observer: string
  messages: number
  lastIngestAt: string | null
}

interface JournalStatus {
  status: 'available' | 'unavailable'
  errorClass?: string
  schemaVersion?: number | null
  observers?: JournalObserverStatus[]
  rejectedSinceStart?: Record<string, number>
  gc?: {
    lastRunAt: string | null
    deletedSinceStart: { messages: number; conversations: number }
    failedSinceStart: number
  }
  ingest?: { samples: number; p50Ms: number | null; p95Ms: number | null }
}

export default function CommsJournalPanel() {
  const [status, setStatus] = useState<JournalStatus | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(() => {
    setLoading(true)
    apiFetch('/api/comms/journal/status')
      .then(async response => {
        if (!response.ok) throw new Error(`Http${response.status}`)
        return response.json() as Promise<JournalStatus>
      })
      .then(setStatus)
      .catch(error => setStatus({ status: 'unavailable', errorClass: error instanceof Error ? error.message : 'FetchError' }))
      .finally(() => setLoading(false))
  }, [])

  useEffect(load, [load])

  const observers = status?.observers ?? []
  const messages = observers.reduce((sum, observer) => sum + observer.messages, 0)
  const rejected = Object.values(status?.rejectedSinceStart ?? {}).reduce((sum, count) => sum + count, 0)

  return (
    <div className="journal-panel">
      <div className="journal-panel-header">
        <span className="section-title">Comms journal</span>
        <button className="row-btn" onClick={load} disabled={loading}>{loading ? '…' : 'refresh'}</button>
      </div>
      {status?.status === 'unavailable' ? (
        <div className="journal-panel-unavailable">unavailable: {status.errorClass ?? 'unknown'}</div>
      ) : loading && !status ? (
        <div className="journal-panel-muted">loading…</div>
      ) : (
        <div className="journal-panel-stats">
          <span>schema {status?.schemaVersion ?? 'n/a'}</span>
          <span>{observers.length} observers</span>
          <span>{messages} messages</span>
          <span>{rejected} rejected</span>
          <span>p95 {status?.ingest?.p95Ms == null ? 'n/a' : `${Math.round(status.ingest.p95Ms)} ms`}</span>
          <span>gc failures {status?.gc?.failedSinceStart ?? 0}</span>
        </div>
      )}
    </div>
  )
}
