import { useState, useMemo } from 'react'
import type { ScheduleSummary, ScheduleDetail, WorkflowTypeInfo } from '../types'
import { apiGet, apiSend, errorMessage } from '../utils'
import CreateScheduleModal from './CreateScheduleModal'
import { useTick } from '../hooks/useTick'
import Icon from './Icon'

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
        <button className="btn confirm-bar-btn confirm-bar-cancel" onClick={onCancel}>Cancel</button>
        <button className="btn btn-danger confirm-bar-btn confirm-bar-confirm" onClick={() => { onConfirm(); onCancel() }}>Confirm</button>
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
    apiGet<ScheduleDetail>(`/api/schedules/${encodeURIComponent(schedule.namespace)}/${encodeURIComponent(schedule.scheduleId)}`)
      .then((d: ScheduleDetail) => setDetail(d))
      .catch((e: unknown) => setDetailError(errorMessage(e)))
      .finally(() => setDetailLoading(false))
  }

  function doAction(action: 'pause' | 'unpause' | 'trigger') {
    setActionState('pending')
    const url = `/api/schedules/${encodeURIComponent(schedule.namespace)}/${action}/${encodeURIComponent(schedule.scheduleId)}`
    apiSend(url, 'POST')
      .then(() => {
        setActionState('success')
        setActionMsg(action === 'pause' ? 'Paused' : action === 'unpause' ? 'Unpaused' : 'Triggered')
        onRefresh()
        // Reload detail if expanded
        if (expanded) { setDetail(null); loadDetail() }
      })
      .catch((e: unknown) => { setActionState('error'); setActionMsg(errorMessage(e)) })
      .finally(() => setTimeout(() => { setActionState('idle'); setActionMsg('') }, 3000))
  }

  function doDelete() {
    setActionState('pending')
    apiSend(`/api/schedules/${encodeURIComponent(schedule.namespace)}/${encodeURIComponent(schedule.scheduleId)}`, 'DELETE')
      .then(() => {
        setActionState('success')
        setActionMsg('Deleted')
        onRefresh()
      })
      .catch((e: unknown) => { setActionState('error'); setActionMsg(errorMessage(e)) })
      .finally(() => setTimeout(() => { setActionState('idle'); setActionMsg('') }, 3000))
  }

  const human = cronHumanReadable(schedule.cronExpression)

  return (
    <div className={`sched-row${schedule.paused ? ' paused' : ''}${expanded ? ' expanded' : ''}`}>
      <div
        className="sched-main"
        role="button" tabIndex={0} aria-expanded={expanded}
        onClick={toggleExpand}
        onKeyDown={e => { if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); toggleExpand() } }}
      >
        <div className="sched-id">{schedule.scheduleId}</div>
        <div className="sched-when">
          <Icon name="clock" size={15} />
          {human && <span>{human}</span>}
          {schedule.cronExpression && <span className="sched-cron" title={human}>{schedule.cronExpression}</span>}
        </div>
        <div className="sched-meta">
          <span className={`pill wf-status-badge ${schedule.paused ? 'wf-status-paused' : 'wf-status-running'}`}>
            {schedule.paused ? 'paused' : 'active'}
          </span>
          <span className="pill wf-ns-badge">{schedule.namespace}</span>
          {schedule.workflowType && <span className="sched-type">{schedule.workflowType}</span>}
          {schedule.memo && <span className="sched-memo">{schedule.memo}</span>}
        </div>
        <div className="sched-actions" onClick={e => e.stopPropagation()}>
          {actionState === 'idle' && (
            <>
              <button
                className="btn btn-s"
                onClick={() => doAction(schedule.paused ? 'unpause' : 'pause')}
                title={schedule.paused ? 'Unpause' : 'Pause'}
              >
                <Icon name={schedule.paused ? 'play' : 'pause'} size={15} />{schedule.paused ? 'Unpause' : 'Pause'}
              </button>
              <button className="btn btn-s" onClick={() => doAction('trigger')} title="Trigger immediate run">
                <Icon name="bolt" size={15} />Trigger
              </button>
              {!confirmDelete && (
                <button
                  className="icon-btn icon-btn-s sched-delete"
                  onClick={() => { setConfirmDelete(true); setTimeout(() => setConfirmDelete(false), 5000) }}
                  title="Delete schedule" aria-label="Delete schedule"
                >
                  <Icon name="trash" size={16} />
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
        <div className="sched-detail">
          {detailLoading && <div className="config-loading">Loading details…</div>}
          {detailError  && <div className="config-feedback config-feedback-error">{detailError}</div>}
          {detail && (
            <>
              {detail.nextRunTime && (
                <div className="row-detail-item">
                  <span className="row-detail-label">next run</span>
                  <span className="row-detail-value">{formatRelativeOrFuture(detail.nextRunTime)} · {new Date(detail.nextRunTime).toLocaleString()}</span>
                </div>
              )}
              {detail.lastRunTime && (
                <div className="row-detail-item">
                  <span className="row-detail-label">last run</span>
                  <span className="row-detail-value">{formatRelativeOrFuture(detail.lastRunTime)} · {new Date(detail.lastRunTime).toLocaleString()}</span>
                </div>
              )}
              {detail.lastRunWorkflowId && (
                <div className="row-detail-item">
                  <span className="row-detail-label">last workflow</span>
                  <span className="row-detail-value mono">{detail.lastRunWorkflowId}</span>
                </div>
              )}
              {detail.input !== null && detail.input !== undefined && JSON.stringify(detail.input) !== '{}' && (
                <div className="row-detail-item">
                  <span className="row-detail-label">input</span>
                  <pre className="sched-input">{JSON.stringify(detail.input, null, 2)}</pre>
                </div>
              )}
            </>
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
  // Next and last runs are shown relative to now ("in 4m"): redraw as the clock moves.
  useTick()
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
        {filterNamespaces.length > 1 && (
          <div className="seg ns-filters" role="group" aria-label="Namespace">
            <button className={`seg-btn ns-btn${nsFilter === 'all' ? ' active' : ''}`} onClick={() => setNsFilter('all')}>all</button>
            {filterNamespaces.map(ns => (
              <button key={ns} className={`seg-btn ns-btn${nsFilter === ns ? ' active' : ''}`} onClick={() => setNsFilter(ns)}>{ns}</button>
            ))}
          </div>
        )}
        <button className="icon-btn" onClick={onRefresh} disabled={loading} title="Refresh" aria-label="Refresh schedules">
          {loading ? '…' : <Icon name="refresh" />}
        </button>
        <button className="btn btn-primary" onClick={() => setShowCreateModal(true)}>
          <Icon name="plus" size={16} />New schedule
        </button>
      </div>

      {loading && schedules.length === 0 && (
        <div className="view-empty">Loading schedules…</div>
      )}

      {!loading && filtered.length === 0 && (
        <div className="view-empty">No schedules found.</div>
      )}

      <div className="list sched-list">
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
