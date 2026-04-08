import { useState, useEffect, useRef, useCallback } from 'react'
import type {
  WorkflowSummary,
  WorkflowEvent,
  SignalDef,
  SignalButton,
  WfActionState,
} from '../types'
import { temporalUiUrl, heartbeatAge, decodeUnicode } from '../utils'
import { PHASE_LABELS } from '../constants'

type CompletedStatusFilter = 'all' | 'failed' | 'completed' | 'canceled' | 'terminated'

const COMPLETED_FILTER_KEY = 'fleet-dashboard-completed-status-filter'

function PhaseChip({ phase }: { phase: string | null }) {
  if (!phase) return null
  const info = PHASE_LABELS[phase]
  const label = info?.label ?? phase
  const color = info?.color ?? 'grey'
  return <span className={`phase-chip phase-${color}`}>{label}</span>
}

// ── ConfirmBar ────────────────────────────────────────────────────────────────

interface PendingConfirm {
  description: string
  onConfirm: () => void
}

function ConfirmBar({ pending, onCancel }: { pending: PendingConfirm; onCancel: () => void }) {
  return (
    <div className="confirm-bar">
      <span className="confirm-bar-desc">{pending.description}</span>
      <div className="confirm-bar-actions">
        <button className="confirm-bar-btn confirm-bar-cancel" onClick={onCancel}>Cancel</button>
        <button className="confirm-bar-btn confirm-bar-confirm" onClick={() => { pending.onConfirm(); onCancel() }}>Confirm</button>
      </div>
    </div>
  )
}

// ── SignalButtons ─────────────────────────────────────────────────────────────

interface SignalButtonsProps {
  wf: WorkflowSummary
  sigDefs: SignalDef[]
  signalStates: Record<string, 'idle' | 'pending' | 'success' | 'error'>
  signalMsg: Record<string, string>
  signalKey: (wf: WorkflowSummary) => string
  sentSignals: Set<string>
  onSignalBtnClick: (wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) => void
}

function SignalButtons({
  wf, sigDefs, signalStates, signalMsg,
  signalKey, sentSignals, onSignalBtnClick,
}: SignalButtonsProps) {
  const sk = signalKey(wf)
  const state = signalStates[sk]

  if (state === 'pending') return <span className="wf-action-feedback wf-action-pending">…</span>
  if (state === 'success') return <span className="wf-action-feedback wf-action-success">{signalMsg[sk]}</span>
  if (state === 'error')   return <span className="wf-action-feedback wf-action-error">{signalMsg[sk]}</span>

  if (sentSignals.has(`${wf.workflowId}::${wf.phase}`)) return null

  if (sigDefs.length === 0) return null

  return (
    <div className="wf-signal-groups">
      {sigDefs.map(sig => (
        <div key={sig.name} className="wf-signal-group">
          <span className="wf-signal-group-label">{sig.label}:</span>
          <div className="wf-signal-btns">
            {sig.buttons.map(btn => (
              <button
                key={`${sig.name}-${btn.label}`}
                className="wf-btn wf-btn-signal wf-btn-signal-prominent"
                title={`Send signal: ${sig.name}`}
                onClick={() => onSignalBtnClick(wf, sig, btn)}
              >
                {btn.label}
              </button>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

// ── WorkflowsView ─────────────────────────────────────────────────────────────

interface WorkflowsViewProps {
  workflows: WorkflowSummary[]
  completedWorkflows: WorkflowSummary[]
  completedCollapsed: boolean
  completedLoading: boolean
  nsFilter: string
  namespaces: string[]
  filteredWorkflows: WorkflowSummary[]
  filteredCompletedWorkflows: WorkflowSummary[]
  agentByWorkflow: Record<string, string>
  wfActionStates: Record<string, WfActionState>
  wfActionMsg: Record<string, string>
  signalStates: Record<string, 'idle' | 'pending' | 'success' | 'error'>
  signalMsg: Record<string, string>
  sentSignals: Set<string>
  signalRegistry: Record<string, SignalDef[]>
  wfMenuOpen: Record<string, boolean>
  selectedWf: WorkflowSummary | null
  wfHistory: WorkflowEvent[]
  wfHistoryLoading: boolean
  wfHistoryError: string | null
  expandedEvents: Set<number>
  onNsFilter: (ns: string) => void
  onToggleCompleted: () => void
  onRefreshCompleted: () => void
  onWfClick: (wf: WorkflowSummary) => void
  onSignalClick: (wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) => void
  onToggleMenu: (k: string) => void
  onToggleEvent: (eventId: number) => void
  onCloseDetail: () => void
  onRefreshDetail: () => void
  getSignalDefs: (wf: WorkflowSummary) => SignalDef[]
  wfActionState: (wf: WorkflowSummary) => WfActionState
  signalKey: (wf: WorkflowSummary) => string
  wfKey: (wf: WorkflowSummary) => string
  wfStatusClass: (wf: WorkflowSummary, signalDefs: SignalDef[]) => string
  onSendSignal: (wf: WorkflowSummary, sigName: string, payload: string) => void
  onExecuteWfAction: (wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') => void
  highlightedEntityId?: string | null
  onClearHighlight?: () => void
  onStartWorkflow: () => void
  onRefreshWorkflows: () => void
  workflowsLoading: boolean
}

export default function WorkflowsView({
  workflows,
  completedCollapsed,
  completedLoading,
  nsFilter,
  namespaces,
  filteredWorkflows,
  filteredCompletedWorkflows,
  agentByWorkflow,
  wfActionMsg,
  signalStates,
  signalMsg,
  sentSignals,
  wfMenuOpen,
  selectedWf,
  wfHistory,
  wfHistoryLoading,
  wfHistoryError,
  expandedEvents,
  onNsFilter,
  onToggleCompleted,
  onRefreshCompleted,
  onWfClick,
  onSignalClick,
  onToggleMenu,
  onToggleEvent,
  onCloseDetail,
  onRefreshDetail,
  getSignalDefs,
  wfActionState,
  signalKey,
  wfKey,
  wfStatusClass,
  onSendSignal,
  onExecuteWfAction,
  highlightedEntityId,
  onClearHighlight,
  onStartWorkflow,
  onRefreshWorkflows,
  workflowsLoading,
}: WorkflowsViewProps) {
  // ── Highlight / scroll-to ────────────────────────────────────────────────
  const rowRefs = useRef<Map<string, HTMLDivElement>>(new Map())
  const setRowRef = useCallback((id: string, el: HTMLDivElement | null) => {
    if (el) rowRefs.current.set(id, el)
    else rowRefs.current.delete(id)
  }, [])

  useEffect(() => {
    if (!highlightedEntityId) return
    const el = rowRefs.current.get(highlightedEntityId)
    if (!el) return
    el.scrollIntoView({ behavior: 'smooth', block: 'center' })
    el.classList.add('highlight-flash')
    const timer = setTimeout(() => {
      el.classList.remove('highlight-flash')
      onClearHighlight?.()
    }, 1500)
    return () => clearTimeout(timer)
  }, [highlightedEntityId, onClearHighlight])

  // ── Local confirm bar state ──────────────────────────────────────────────
  const [pendingConfirm, setPendingConfirm] = useState<PendingConfirm | null>(null)

  // ── Completed status filter ───────────────────────────────────────────────
  const [completedStatusFilter, setCompletedStatusFilter] = useState<CompletedStatusFilter>(() => {
    try { return (localStorage.getItem(COMPLETED_FILTER_KEY) as CompletedStatusFilter) ?? 'all' }
    catch { return 'all' }
  })

  function setAndPersistFilter(f: CompletedStatusFilter) {
    setCompletedStatusFilter(f)
    try { localStorage.setItem(COMPLETED_FILTER_KEY, f) } catch { /* ignore */ }
  }

  const displayedCompletedWorkflows = completedStatusFilter === 'all'
    ? filteredCompletedWorkflows
    : filteredCompletedWorkflows.filter(wf => wf.status.toLowerCase() === completedStatusFilter)

  const hasFailedWorkflows = filteredCompletedWorkflows.some(wf => wf.status.toLowerCase() === 'failed')

  // Auto-dismiss confirm bar after 5 seconds
  useEffect(() => {
    if (!pendingConfirm) return
    const id = setTimeout(() => setPendingConfirm(null), 5000)
    return () => clearTimeout(id)
  }, [pendingConfirm])

  function handleSignalBtnClick(wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) {
    if (btn.requiresComment) {
      // Delegate to App.tsx to open the comment modal
      onSignalClick(wf, sig, btn)
      return
    }
    const phaseInfo = wf.phase ? PHASE_LABELS[wf.phase] : null
    const phaseLabel = phaseInfo?.label ?? wf.phase ?? ''
    let desc = `${btn.label} for ${wf.workflowType}`
    if (phaseLabel) desc += ` (${phaseLabel})`
    if (wf.prNumber) desc += ` — PR #${wf.prNumber}`
    else if (wf.issueNumber) desc += ` — #${wf.issueNumber}`
    setPendingConfirm({
      description: desc,
      onConfirm: () => onSendSignal(wf, sig.name, btn.payload),
    })
  }

  function handleWfActionLocal(wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') {
    const label = action === 'cancel' ? 'Cancel' : action === 'restart' ? 'Restart' : 'Terminate'
    setPendingConfirm({
      description: `${label} workflow "${wf.workflowType}"?`,
      onConfirm: () => onExecuteWfAction(wf, action),
    })
    // Close the dropdown menu if open
    onToggleMenu(wfKey(wf) + '__close__')
  }

  // All signal-waiting workflows, cross-namespace — for the attention section
  const attentionWorkflows = workflows.filter(wf => getSignalDefs(wf).length > 0)

  // Main table excludes signal-waiting workflows (they show in attention section instead)
  const mainTableWorkflows = filteredWorkflows.filter(wf => getSignalDefs(wf).length === 0)

  return (
    <div className="view-page">
      {/* ── Header ── */}
      <div className="view-page-header">
        <h1 className="view-page-title">
          Workflows
          {workflows.length > 0 && <span className="section-count">{workflows.length}</span>}
        </h1>
        <div className="wf-header-right">
          {namespaces.length > 0 && (
            <div className="ns-filters">
              <button
                className={`ns-btn${nsFilter === 'all' ? ' active' : ''}`}
                onClick={() => onNsFilter('all')}
              >all</button>
              {namespaces.map(ns => (
                <button
                  key={ns}
                  className={`ns-btn${nsFilter === ns ? ' active' : ''}`}
                  onClick={() => onNsFilter(ns)}
                >{ns}</button>
              ))}
            </div>
          )}
          <button className="completed-refresh-btn" onClick={onRefreshWorkflows} disabled={workflowsLoading} title="Refresh workflows">↻</button>
          <button className="wf-start-btn" onClick={onStartWorkflow} title="Start a new workflow">+ Start</button>
        </div>
      </div>

      {/* ── Needs your attention ── */}
      <div className="attention-section">
        <div className="attention-header">
          Needs your attention
          {attentionWorkflows.length > 0 && (
            <span className="attention-count">{attentionWorkflows.length}</span>
          )}
        </div>
        {attentionWorkflows.length === 0 ? (
          <div className="attention-empty">Nothing needs your attention</div>
        ) : (
          <div className="attention-cards">
            {attentionWorkflows.map(wf => {
              const sigDefs = getSignalDefs(wf)
              return (
                <div key={`attention-${wf.namespace}/${wf.workflowId}`} className="attention-card">
                  <div className="attention-card-meta">
                    <span className="attention-wf-type" onClick={() => onWfClick(wf)} title="Click to view execution history">
                      {wf.workflowType}
                    </span>
                    <PhaseChip phase={wf.phase} />
                    <span className="wf-ns-badge">{wf.namespace}</span>
                    {wf.issueNumber && wf.repo && (
                      <a href={`https://github.com/${wf.repo}/issues/${wf.issueNumber}`} target="_blank" rel="noreferrer" className="wf-link" title={`GitHub issue #${wf.issueNumber}`}>#{wf.issueNumber}</a>
                    )}
                    {wf.prNumber && wf.repo && (
                      <a href={`https://github.com/${wf.repo}/pull/${wf.prNumber}`} target="_blank" rel="noreferrer" className="wf-link" title={`GitHub PR #${wf.prNumber}`}>PR #{wf.prNumber}</a>
                    )}
                    {wf.docPrs && wf.docPrs.split(/\s+/).map(ref => {
                      const m = ref.match(/^([\w-]+\/[\w-]+)#(\d+)$/)
                      if (!m) return null
                      return (
                        <a key={ref} href={`https://github.com/${m[1]}/pull/${m[2]}`} target="_blank" rel="noreferrer" className="wf-link" title={`Doc PR ${ref}`}>doc #{m[2]}</a>
                      )
                    })}
                  </div>
                  <div className="attention-card-actions">
                    <SignalButtons
                      wf={wf}
                      sigDefs={sigDefs}
                      signalStates={signalStates}
                      signalMsg={signalMsg}
                      signalKey={signalKey}
                      sentSignals={sentSignals}
                      onSignalBtnClick={handleSignalBtnClick}
                    />
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {/* ── Active workflows ── */}
      {mainTableWorkflows.length === 0 ? (
        <div className="wf-empty">
          {workflows.length === 0
            ? 'No running workflows. Temporal poller may not be configured.'
            : attentionWorkflows.length > 0 && filteredWorkflows.length === attentionWorkflows.filter(wf => nsFilter === 'all' || wf.namespace === nsFilter).length
            ? 'All active workflows need your attention (see above).'
            : 'No workflows in this namespace.'}
        </div>
      ) : (
        <div className="wf-table">
          {mainTableWorkflows.map(wf => {
            const url = temporalUiUrl(wf)
            const shortId = wf.workflowId.length > 40
              ? wf.workflowId.slice(0, 38) + '…'
              : wf.workflowId
            const ws = wfActionState(wf)
            const wfMsg = wfActionMsg[wfKey(wf)] ?? ''
            const sigDefs = getSignalDefs(wf)
            const wfClass = wfStatusClass(wf, sigDefs)
            const k = wfKey(wf)
            const menuOpen = wfMenuOpen[k] ?? false

            return (
              <div key={`${wf.namespace}/${wf.workflowId}`} ref={el => setRowRef(wf.workflowId, el)} className={`wf-row ${wfClass}`}>
                <div className="wf-type wf-type-clickable" onClick={() => onWfClick(wf)} title="Click to view execution history">{wf.workflowType}</div>
                <div className="wf-id" title={wf.workflowId}>
                  {url
                    ? <a href={url} target="_blank" rel="noreferrer" className="wf-link">{shortId}</a>
                    : shortId}
                </div>
                <div className="wf-meta">
                  <span className="wf-ns-badge">{wf.namespace}</span>
                  <PhaseChip phase={wf.phase} />
                  {wf.taskQueue && wf.taskQueue !== wf.namespace && <span className="wf-queue">{wf.taskQueue}</span>}
                  {agentByWorkflow[wf.workflowId] && (
                    <span className="wf-agent-badge" title="Executing agent">
                      {agentByWorkflow[wf.workflowId]}
                    </span>
                  )}
                  {wf.issueNumber && wf.repo && (
                    <a href={`https://github.com/${wf.repo}/issues/${wf.issueNumber}`} target="_blank" rel="noreferrer" className="wf-link" title={`GitHub issue #${wf.issueNumber}`}>#{wf.issueNumber}</a>
                  )}
                  {wf.prNumber && wf.repo && (
                    <a href={`https://github.com/${wf.repo}/pull/${wf.prNumber}`} target="_blank" rel="noreferrer" className="wf-link" title={`GitHub PR #${wf.prNumber}`}>PR #{wf.prNumber}</a>
                  )}
                  {wf.docPrs && wf.docPrs.split(/\s+/).map(ref => {
                    const m = ref.match(/^([\w-]+\/[\w-]+)#(\d+)$/)
                    if (!m) return null
                    return (
                      <a key={ref} href={`https://github.com/${m[1]}/pull/${m[2]}`} target="_blank" rel="noreferrer" className="wf-link" title={`Doc PR ${ref}`}>doc #{m[2]}</a>
                    )
                  })}
                  <span className="wf-age">{heartbeatAge(wf.startTime)}</span>
                </div>
                <div className="wf-actions">
                  {ws === 'idle' && (
                    <>
                      {/* Desktop: dots menu */}
                      <div className="wf-secondary-actions wf-actions-desktop">
                        <button
                          className="wf-btn wf-btn-more"
                          title="More actions"
                          onClick={() => onToggleMenu(k)}
                        >
                          {menuOpen ? '✕' : '···'}
                        </button>
                        {menuOpen && (
                          <div className="wf-more-menu">
                            <button
                              className="wf-btn wf-btn-cancel"
                              onClick={() => handleWfActionLocal(wf, 'cancel')}
                            >cancel</button>
                            <button
                              className="wf-btn wf-btn-restart"
                              onClick={() => handleWfActionLocal(wf, 'restart')}
                            >restart</button>
                            <button
                              className="wf-btn wf-btn-terminate"
                              onClick={() => handleWfActionLocal(wf, 'terminate')}
                            >kill</button>
                          </div>
                        )}
                      </div>
                      {/* Mobile: labeled buttons inline */}
                      <div className="wf-labeled-actions wf-actions-mobile">
                        <button
                          className="wf-btn wf-btn-cancel"
                          onClick={() => handleWfActionLocal(wf, 'cancel')}
                        >Cancel</button>
                        <button
                          className="wf-btn wf-btn-restart"
                          onClick={() => handleWfActionLocal(wf, 'restart')}
                        >Restart</button>
                        <button
                          className="wf-btn wf-btn-terminate"
                          onClick={() => handleWfActionLocal(wf, 'terminate')}
                        >Kill</button>
                      </div>
                    </>
                  )}
                  {ws === 'pending' && <span className="wf-action-feedback wf-action-pending">…</span>}
                  {ws === 'success' && <span className="wf-action-feedback wf-action-success">{wfMsg}</span>}
                  {ws === 'error' && <span className="wf-action-feedback wf-action-error">{wfMsg}</span>}
                </div>
              </div>
            )
          })}
        </div>
      )}

      {/* ── Recently Completed ── */}
      <div className="completed-header" onClick={onToggleCompleted}>
        <span className="completed-toggle">{completedCollapsed ? '▸' : '▾'}</span>
        <span>Recently Completed</span>
        <span className="completed-count">{displayedCompletedWorkflows.length}</span>
        <button
          className="completed-refresh-btn"
          onClick={e => { e.stopPropagation(); onRefreshCompleted() }}
          disabled={completedLoading}
          title="Refresh completed workflows"
        >{completedLoading ? '…' : '↻'}</button>
      </div>
      {!completedCollapsed && (
        <div className="completed-filters" onClick={e => e.stopPropagation()}>
          {(['all', 'failed', 'completed', 'canceled', 'terminated'] as CompletedStatusFilter[]).map(f => (
            <button
              key={f}
              className={`completed-filter-btn${completedStatusFilter === f ? ' active' : ''}`}
              onClick={() => setAndPersistFilter(f)}
            >
              {f === 'all' ? 'All' : f.charAt(0).toUpperCase() + f.slice(1)}
              {f === 'failed' && hasFailedWorkflows && <span className="completed-filter-warn">⚠</span>}
            </button>
          ))}
        </div>
      )}
      {!completedCollapsed && (
        <div className="wf-table">
          {displayedCompletedWorkflows.length === 0 ? (
            <div className="wf-empty">No recently completed workflows.</div>
          ) : (
            displayedCompletedWorkflows.map(wf => {
              const url = temporalUiUrl(wf)
              const shortId = wf.workflowId.length > 40
                ? wf.workflowId.slice(0, 38) + '…'
                : wf.workflowId
              const statusCls = wf.status.toLowerCase()
              return (
                <div
                  key={`completed-${wf.namespace}/${wf.workflowId}`}
                  className="wf-row wf-row-muted"
                  onClick={() => onWfClick(wf)}
                  title="Click to view execution history"
                  style={{ cursor: 'pointer' }}
                >
                  <div className="wf-type">{wf.workflowType}</div>
                  <div className="wf-id" title={wf.workflowId}>
                    {url
                      ? <a href={url} target="_blank" rel="noreferrer" className="wf-link" onClick={e => e.stopPropagation()}>{shortId}</a>
                      : shortId}
                  </div>
                  <div className="wf-meta">
                    <span className="wf-ns">{wf.namespace}</span>
                    <span className={`wf-status-badge wf-status-${statusCls}`}>{statusCls}</span>
                    {wf.issueNumber && wf.repo && (
                      <a href={`https://github.com/${wf.repo}/issues/${wf.issueNumber}`} target="_blank" rel="noreferrer" className="wf-link" onClick={e => e.stopPropagation()} title={`GitHub issue #${wf.issueNumber}`}>#{wf.issueNumber}</a>
                    )}
                    {wf.prNumber && wf.repo && (
                      <a href={`https://github.com/${wf.repo}/pull/${wf.prNumber}`} target="_blank" rel="noreferrer" className="wf-link" onClick={e => e.stopPropagation()} title={`GitHub PR #${wf.prNumber}`}>PR #{wf.prNumber}</a>
                    )}
                    {wf.docPrs && wf.docPrs.split(/\s+/).map(ref => {
                      const m = ref.match(/^([\w-]+\/[\w-]+)#(\d+)$/)
                      if (!m) return null
                      return (
                        <a key={ref} href={`https://github.com/${m[1]}/pull/${m[2]}`} target="_blank" rel="noreferrer" className="wf-link" onClick={e => e.stopPropagation()} title={`Doc PR ${ref}`}>doc #{m[2]}</a>
                      )
                    })}
                    <span className="wf-age">{heartbeatAge(wf.startTime)}</span>
                  </div>
                  <div className="wf-actions">
                    {wf.closeTime && (
                      <span className="wf-close-time" title={new Date(wf.closeTime).toLocaleString()}>
                        closed {heartbeatAge(wf.closeTime)}
                      </span>
                    )}
                    {(() => {
                      const ws = wfActionState(wf)
                      const wfMsg = wfActionMsg[wfKey(wf)] ?? ''
                      return (
                        <>
                          {ws === 'idle' && (
                            <button
                              className="wf-btn wf-btn-restart"
                              onClick={e => { e.stopPropagation(); handleWfActionLocal(wf, 'restart') }}
                            >restart</button>
                          )}
                          {ws === 'pending' && <span className="wf-action-feedback wf-action-pending">…</span>}
                          {ws === 'success' && <span className="wf-action-feedback wf-action-success">{wfMsg}</span>}
                          {ws === 'error' && <span className="wf-action-feedback wf-action-error">{wfMsg}</span>}
                        </>
                      )
                    })()}
                  </div>
                </div>
              )
            })
          )}
        </div>
      )}

      {/* ── Workflow detail panel ── */}
      {selectedWf && (
        <div className="wf-detail-overlay" onClick={onCloseDetail}>
          <div className="wf-detail-modal" onClick={e => e.stopPropagation()}>
            <div className="wf-detail-header">
              <span className="wf-detail-title">
                {selectedWf.workflowType} / <span className="wf-detail-id">{selectedWf.workflowId}</span>
              </span>
              <div className="wf-detail-header-actions">
                <button className="wf-detail-refresh" onClick={onRefreshDetail} disabled={wfHistoryLoading} title="Refresh history">↻</button>
                <button className="wf-detail-close" onClick={onCloseDetail}>✕</button>
              </div>
            </div>
            <div className="wf-detail-body">
              {wfHistoryLoading && <div className="wf-detail-loading">Loading history…</div>}
              {wfHistoryError && <div className="wf-detail-error">Error: {wfHistoryError}</div>}
              {!wfHistoryLoading && !wfHistoryError && wfHistory.length === 0 && (
                <div className="wf-detail-empty">No history events found.</div>
              )}
              {(() => {
                const agentByActivityType = new Map<string, string>()
                for (const e of wfHistory) {
                  if (e.eventType === 'ActivityScheduled' && e.agent && e.activityType) {
                    agentByActivityType.set(e.activityType, e.agent)
                  }
                }
                return wfHistory.map(ev => {
                  const agent = ev.agent ?? (ev.activityType ? agentByActivityType.get(ev.activityType) ?? null : null)
                  const expanded = expandedEvents.has(ev.eventId)
                  const hasDetail = !!(ev.inputSummary || ev.outputSummary || ev.failureMessage)
                  return (
                    <div key={ev.eventId} className={`wf-ev wf-ev-${ev.eventType.toLowerCase()}`}>
                      <div
                        className="wf-ev-header"
                        onClick={hasDetail ? () => onToggleEvent(ev.eventId) : undefined}
                        style={hasDetail ? { cursor: 'pointer' } : undefined}
                      >
                        <span className="wf-ev-type">{ev.eventType}</span>
                        {ev.activityType && <span className="wf-ev-activity">{ev.activityType}</span>}
                        {agent && <span className="wf-ev-agent">→ {agent}</span>}
                        {ev.signalName && <span className="wf-ev-signal">{ev.signalName}</span>}
                        {ev.failureMessage && !hasDetail && <span className="wf-ev-failure">{ev.failureMessage}</span>}
                        <span className="wf-ev-time">{new Date(ev.timestamp).toLocaleTimeString()}</span>
                        {hasDetail && <span className="wf-ev-expand">{expanded ? '▲' : '▼'}</span>}
                      </div>
                      {expanded && hasDetail && (
                        <div className="wf-ev-detail">
                          {ev.inputSummary && (
                            <div className="wf-ev-section">
                              <div className="wf-ev-section-label">Input</div>
                              <pre className="wf-ev-payload">{decodeUnicode(ev.inputSummary)}</pre>
                            </div>
                          )}
                          {ev.outputSummary && (
                            <div className="wf-ev-section">
                              <div className="wf-ev-section-label">Output</div>
                              <pre className="wf-ev-payload">{decodeUnicode(ev.outputSummary)}</pre>
                            </div>
                          )}
                          {ev.failureMessage && (
                            <div className="wf-ev-section">
                              <div className="wf-ev-section-label">Failure</div>
                              <pre className="wf-ev-payload">{decodeUnicode(ev.failureMessage)}</pre>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  )
                })
              })()}
            </div>
          </div>
        </div>
      )}

      {/* ── Confirm bar ── */}
      {pendingConfirm && (
        <ConfirmBar pending={pendingConfirm} onCancel={() => setPendingConfirm(null)} />
      )}
    </div>
  )
}
