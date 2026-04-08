import { useState, useMemo } from 'react'
import type { ScheduleSummary, ScheduleDetail, WorkflowTypeInfo } from '../types'
import { apiFetch } from '../utils'
import CreateScheduleModal from './CreateScheduleModal'

function formatRelativeOrFuture(iso: string): string {
  const diff = new Date(iso).getTime() - Date.now()
  if (diff > 0) {
    const secs = Math.floor(diff / 1000)
    if (secs < 60)  return `in ${secs}s`
    const mins = Math.floor(secs / 60)
    if (mins < 60)  return `in ${mins}m`
    const hours = Math.floor(mins / 60)
    if (hours < 24) return `in ${hours}h ${mins % 60}m`
    return `in ${Math.floor(hours / 24)}d`
  }
  const secs = Math.floor(-diff / 1000)
  if (secs < 60)  return `${secs}s ago`
  const mins = Math.floor(secs / 60)
  if (mins < 60)  return `${mins}m ago`
  const hours = Math.floor(mins / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function cronHumanReadable(cron: string | null): string {
  if (!cron) return ''
  const parts = cron.trim().split(/\s+/)
  if (parts.length < 5) return cron
  const [min, hour, dom, , dow] = parts
  if (min === '*' && hour === '*') return 'every minute'
  if (min.startsWith('*/')) return `every ${min.slice(2)} minutes`
  if (hour === '*') return `every hour at :${min.padStart(2, '0')}`
  const hhmm = `${hour.padStart(2, '0')}:${min.padStart(2, '0')} UTC`
  if (dom === '*' && dow === '*') return `daily at ${hhmm}`
  const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
  if (dow !== '*') return `weekly on ${days[parseInt(dow)] ?? dow} at ${hhmm}`
  return cron
}

// ── ConfirmBar (local copy to avoid coupling) ─────────────────────────────────

function ConfirmBar({ description, onConfirm, onCancel }: {
  description: string
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <div className="confirm-bar">
      <span className="confirm-bar-desc">{description}</span>
      <div className="confirm-bar-actions">
        <button className="confirm-bar-btn confirm-bar-cancel" onClick={onCancel}>Cancel</button>
        <button className="confirm-bar-btn confirm-bar-confirm" onClick={() => { onConfirm(); onCancel() }}>Confirm</button>
      </div>
    </div>
  )
}

// ── ScheduleCard ──────────────────────────────────────────────────────────────

interface ScheduleCardProps {
  schedule: ScheduleSummary
  onRefresh: () => void
}

function ScheduleCard({ schedule, onRefresh }: ScheduleCardProps) {
  const [expanded, setExpanded] = useState(false)
  const [detail, setDetail] = useState<ScheduleDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)

  const [actionState, setActionState] = useState<'idle' | 'pending' | 'success' | 'error'>('idle')
  const [actionMsg, setActionMsg] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)

  function toggleExpand() {
    const next = !expanded
    setExpanded(next)
    if (next && !detail && !detailLoading) loadDetail()
  }

  function loadDetail() {
    setDetailLoading(true)
    setDetailError(null)
    apiFetch(`/api/schedules/${encodeURIComponent(schedule.namespace)}/${encodeURIComponent(schedule.scheduleId)}`)
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then((d: ScheduleDetail) => setDetail(d))
      .catch((e: Error) => setDetailError(e.message))
      .finally(() => setDetailLoading(false))
  }

  function doAction(action: 'pause' | 'unpause' | 'trigger') {
    setActionState('pending')
    const url = `/api/schedules/${encodeURIComponent(schedule.namespace)}/${action}/${encodeURIComponent(schedule.scheduleId)}`
    apiFetch(url, { method: 'POST' })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        setActionState('success')
        setActionMsg(action === 'pause' ? 'Paused' : action === 'unpause' ? 'Unpaused' : 'Triggered')
        onRefresh()
        // Reload detail if expanded
        if (expanded) { setDetail(null); loadDetail() }
      })
      .catch((e: Error) => { setActionState('error'); setActionMsg(e.message) })
      .finally(() => setTimeout(() => { setActionState('idle'); setActionMsg('') }, 3000))
  }

  function doDelete() {
    setActionState('pending')
    apiFetch(`/api/schedules/${encodeURIComponent(schedule.namespace)}/${encodeURIComponent(schedule.scheduleId)}`, { method: 'DELETE' })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        setActionState('success')
        setActionMsg('Deleted')
        onRefresh()
      })
      .catch((e: Error) => { setActionState('error'); setActionMsg(e.message) })
      .finally(() => setTimeout(() => { setActionState('idle'); setActionMsg('') }, 3000))
  }

  const human = cronHumanReadable(schedule.cronExpression)

  return (
    <div>
      <div className="wf-row wf-row-flex" onClick={toggleExpand} style={{ cursor: 'pointer' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0 }}>
          <span className="wf-id">{schedule.scheduleId}</span>
          <span className="wf-ns-badge">{schedule.namespace}</span>
          <span className={`wf-status-badge ${schedule.paused ? 'wf-status-paused' : 'wf-status-running'}`}>
            {schedule.paused ? 'paused' : 'active'}
          </span>
          {schedule.workflowType && (
            <span className="wf-queue">{schedule.workflowType}</span>
          )}
          {schedule.cronExpression && (
            <span className="wf-queue" title={human}>{schedule.cronExpression}</span>
          )}
          {schedule.memo && (
            <span className="wf-task-summary" style={{ color: 'var(--text-muted)', fontSize: '0.78rem' }}>{schedule.memo}</span>
          )}
        </div>
        <div className="wf-actions" onClick={e => e.stopPropagation()}>
          {actionState === 'idle' && (
            <>
              <button
                className="wf-btn wf-btn-signal"
                onClick={() => doAction(schedule.paused ? 'unpause' : 'pause')}
                title={schedule.paused ? 'Unpause' : 'Pause'}
              >
                {schedule.paused ? '▶ Unpause' : '⏸ Pause'}
              </button>
              <button
                className="wf-btn wf-btn-signal"
                onClick={() => doAction('trigger')}
                title="Trigger immediate run"
              >
                ⚡ Trigger
              </button>
              {!confirmDelete && (
                <button
                  className="wf-btn wf-btn-cancel"
                  onClick={() => { setConfirmDelete(true); setTimeout(() => setConfirmDelete(false), 5000) }}
                  title="Delete schedule"
                >
                  ✕ Delete
                </button>
              )}
            </>
          )}
          {actionState === 'pending' && <span className="wf-action-feedback wf-action-pending">…</span>}
          {actionState === 'success' && <span className="wf-action-feedback wf-action-success">{actionMsg}</span>}
          {actionState === 'error'   && <span className="wf-action-feedback wf-action-error">{actionMsg}</span>}
        </div>
      </div>

      {confirmDelete && (
        <ConfirmBar
          description={`Delete schedule "${schedule.scheduleId}"?`}
          onConfirm={doDelete}
          onCancel={() => setConfirmDelete(false)}
        />
      )}

      {expanded && (
        <div className="wf-detail" style={{ padding: '8px 12px' }}>
          {detailLoading && <div className="wf-detail-loading">Loading details…</div>}
          {detailError  && <div className="wf-detail-error">{detailError}</div>}
          {detail && (
            <div style={{ fontSize: '0.82rem', color: 'var(--text-muted)' }}>
              {detail.nextRunTime && (
                <div><strong>Next run:</strong> {formatRelativeOrFuture(detail.nextRunTime)} ({new Date(detail.nextRunTime).toLocaleString()})</div>
              )}
              {detail.lastRunTime && (
                <div><strong>Last run:</strong> {formatRelativeOrFuture(detail.lastRunTime)} ({new Date(detail.lastRunTime).toLocaleString()})</div>
              )}
              {detail.lastRunWorkflowId && (
                <div>
                  <strong>Last workflow:</strong>{' '}
                  <span style={{ fontFamily: 'monospace', fontSize: '0.78rem' }}>{detail.lastRunWorkflowId}</span>
                </div>
              )}
              {human && <div><strong>Schedule:</strong> {human}</div>}
              {detail.input !== null && detail.input !== undefined && JSON.stringify(detail.input) !== '{}' && (
                <div>
                  <strong>Input:</strong>
                  <pre style={{ margin: '2px 0 0', fontSize: '0.76rem', overflowX: 'auto' }}>
                    {JSON.stringify(detail.input, null, 2)}
                  </pre>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

// ── SchedulesView ─────────────────────────────────────────────────────────────

interface SchedulesViewProps {
  schedules: ScheduleSummary[]
  loading: boolean
  workflowTypes: WorkflowTypeInfo[]
  namespaces: string[]
  onRefresh: () => void
}

export default function SchedulesView({ schedules, loading, workflowTypes, namespaces, onRefresh }: SchedulesViewProps) {
  const [nsFilter, setNsFilter] = useState('all')
  const [showCreateModal, setShowCreateModal] = useState(false)

  const filterNamespaces = useMemo(() => {
    const ns = new Set(schedules.map(s => s.namespace))
    return Array.from(ns).sort()
  }, [schedules])

  const filtered = useMemo(() => {
    if (nsFilter === 'all') return schedules
    return schedules.filter(s => s.namespace === nsFilter)
  }, [schedules, nsFilter])

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Schedules
          <span className="section-count">{filtered.length}</span>
        </h1>
        <div className="wf-header-right">
          <button className="wf-btn wf-btn-signal" onClick={() => setShowCreateModal(true)}>
            + New Schedule
          </button>
          <button className="wf-btn" onClick={onRefresh} disabled={loading} title="Refresh">
            {loading ? '…' : '⟳'}
          </button>
        </div>
      </div>

      {/* Namespace filter pills */}
      {filterNamespaces.length > 1 && (
        <div className="ns-filters">
          <button
            className={`ns-btn${nsFilter === 'all' ? ' active' : ''}`}
            onClick={() => setNsFilter('all')}
          >
            all
          </button>
          {filterNamespaces.map(ns => (
            <button
              key={ns}
              className={`ns-btn${nsFilter === ns ? ' active' : ''}`}
              onClick={() => setNsFilter(ns)}
            >
              {ns}
            </button>
          ))}
        </div>
      )}

      {loading && schedules.length === 0 && (
        <div className="view-empty">Loading schedules…</div>
      )}

      {!loading && filtered.length === 0 && (
        <div className="view-empty">No schedules found.</div>
      )}

      <div className="wf-table">
        {filtered.map(s => (
          <ScheduleCard
            key={`${s.namespace}/${s.scheduleId}`}
            schedule={s}
            onRefresh={onRefresh}
          />
        ))}
      </div>

      {showCreateModal && (
        <CreateScheduleModal
          workflowTypes={workflowTypes}
          namespaces={namespaces}
          onClose={() => setShowCreateModal(false)}
          onCreated={() => { setShowCreateModal(false); onRefresh() }}
        />
      )}
    </div>
  )
}
