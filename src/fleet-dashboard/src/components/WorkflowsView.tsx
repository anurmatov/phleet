import { useState, useEffect, useRef, useCallback } from 'react'
import type { WorkflowSummary, SignalDef, SignalButton } from '../types'
import type { WorkflowActions } from '../hooks/useWorkflowActions'
import type { WorkflowLists } from '../hooks/useWorkflowLists'
import { useTick } from '../hooks/useTick'
import { temporalUiUrl, heartbeatAge } from '../utils'
import { PHASE_LABELS } from '../constants'
import WorkflowHistoryPanel from './WorkflowHistoryPanel'
import Icon from './Icon'
import { PhaseChip, WorkflowRefs, shortWorkflowId } from './WorkflowBits'

type CompletedStatusFilter = 'all' | 'failed' | 'completed' | 'canceled' | 'terminated'

const COMPLETED_FILTER_KEY = 'fleet-dashboard-completed-status-filter'

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
        <button className="btn confirm-bar-btn confirm-bar-cancel" onClick={onCancel}>Cancel</button>
        <button className="btn btn-primary confirm-bar-btn confirm-bar-confirm" onClick={() => { pending.onConfirm(); onCancel() }}>Confirm</button>
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
                className="btn btn-s btn-primary wf-btn wf-btn-signal"
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
  /** The running workflows, kept current by the dashboard socket. */
  workflows: WorkflowSummary[]
  /** Filter, completed runs and manual re-reads (hooks/useWorkflowLists.ts). */
  lists: WorkflowLists
  /** Signals, cancel/restart/kill and the history panel (hooks/useWorkflowActions.ts). */
  actions: WorkflowActions
  agentByWorkflow: Record<string, string>
  highlightedEntityId?: string | null
  onClearHighlight?: () => void
}

export default function WorkflowsView({
  workflows, lists, actions, agentByWorkflow, highlightedEntityId, onClearHighlight,
}: WorkflowsViewProps) {
  const {
    completedCollapsed, completedLoading, nsFilter, namespaces, filteredWorkflows, filteredCompletedWorkflows,
    workflowsLoading,
    setNsFilter: onNsFilter,
    toggleCompleted: onToggleCompleted,
    refreshCompleted: onRefreshCompleted,
    refreshWorkflows: onRefreshWorkflows,
  } = lists
  const onStartWorkflow = () => lists.setStartOpen(true)
  const {
    wfActionMsg, signalStates, signalMsg, sentSignals,
    menuOpen: wfMenuOpen,
    openDetail: onWfClick,
    signalClick: onSignalClick,
    toggleMenu: onToggleMenu,
    sendSignal: onSendSignal,
    runAction: onExecuteWfAction,
    getSignalDefs, wfActionState, signalKey, wfKey, wfStatusClass,
  } = actions
  // The rows show ages ("3m ago"): redraw as the clock moves.
  useTick()

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
    if (btn.requiresComment || sig.name === 'request-input') {
      // Exact-request decisions must load their run-bound content in the modal.
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
        <button className="icon-btn" onClick={onRefreshWorkflows} disabled={workflowsLoading} title="Refresh workflows" aria-label="Refresh workflows">
          <Icon name="refresh" />
        </button>
        <button className="btn btn-primary wf-start-btn" onClick={onStartWorkflow} title="Start a new workflow">
          <Icon name="plus" size={16} />Start
        </button>
      </div>

      {/* ── Needs you ── */}
      {/* Only when something does. An empty box saying nothing is waiting is one more thing to read. */}
      {attentionWorkflows.length > 0 && (
        <div className="attention-section">
          <div className="section-header attention-header">
            <Icon name="hand" size={18} />
            <span className="section-title">Waiting for you</span>
            <span className="section-count attention-count">{attentionWorkflows.length}</span>
          </div>
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
                    <span className="pill wf-ns-badge">{wf.namespace}</span>
                    <WorkflowRefs wf={wf} />
                    <span className="wf-age">{heartbeatAge(wf.startTime)}</span>
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
        </div>
      )}

      {/* ── Running ── */}
      <div className="section-header">
        <span className="section-title">Running</span>
        {mainTableWorkflows.length > 0 && <span className="section-count">{mainTableWorkflows.length}</span>}
        {namespaces.length > 1 && (
          <div className="seg ns-filters" role="group" aria-label="Namespace">
            <button className={`seg-btn ns-btn${nsFilter === 'all' ? ' active' : ''}`} onClick={() => onNsFilter('all')}>all</button>
            {namespaces.map(ns => (
              <button key={ns} className={`seg-btn ns-btn${nsFilter === ns ? ' active' : ''}`} onClick={() => onNsFilter(ns)}>{ns}</button>
            ))}
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
            const shortId = shortWorkflowId(wf.workflowId)
            const ws = wfActionState(wf)
            const wfMsg = wfActionMsg[wfKey(wf)] ?? ''
            const sigDefs = getSignalDefs(wf)
            const wfClass = wfStatusClass(wf, sigDefs)
            const k = wfKey(wf)
            const menuOpen = wfMenuOpen[k] ?? false

            return (
              <div key={`${wf.namespace}/${wf.workflowId}`} ref={el => setRowRef(wf.workflowId, el)} className={`wf-row ${wfClass}`}>
                <div className="wf-type wf-type-clickable" onClick={() => onWfClick(wf)} title="Click to view execution history">{wf.workflowType}</div>
                <div className="wf-meta">
                  <PhaseChip phase={wf.phase} />
                  <span className="pill wf-ns-badge">{wf.namespace}</span>
                  {wf.taskQueue && wf.taskQueue !== wf.namespace && <span className="pill wf-queue">{wf.taskQueue}</span>}
                  {agentByWorkflow[wf.workflowId] && (
                    <span className="pill pill-info wf-agent-badge" title="Executing agent">
                      {agentByWorkflow[wf.workflowId]}
                    </span>
                  )}
                  <WorkflowRefs wf={wf} />
                  <span className="wf-age">{heartbeatAge(wf.startTime)}</span>
                </div>
                <div className="wf-id" title={wf.workflowId}>
                  {url
                    ? <a href={url} target="_blank" rel="noreferrer" className="wf-link">{shortId}</a>
                    : shortId}
                </div>
                <div className="wf-actions">
                  {ws === 'idle' && (
                    <div className="wf-secondary-actions wf-actions-desktop">
                      <button
                        className={`icon-btn wf-btn-more${menuOpen ? ' active' : ''}`}
                        title="More actions" aria-label="More actions" aria-expanded={menuOpen}
                        onClick={() => onToggleMenu(k)}
                      >
                        <Icon name={menuOpen ? 'close' : 'more'} />
                      </button>
                      {menuOpen && (
                        <div className="menu wf-more-menu">
                          <button className="menu-item" onClick={() => handleWfActionLocal(wf, 'cancel')}><Icon name="cancel" size={16} />Cancel</button>
                          <button className="menu-item" onClick={() => handleWfActionLocal(wf, 'restart')}><Icon name="restart" size={16} />Restart</button>
                          <button className="menu-item menu-item-danger" onClick={() => handleWfActionLocal(wf, 'terminate')}><Icon name="stop" size={16} />Kill</button>
                        </div>
                      )}
                    </div>
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

      {/* ── Recently completed ── */}
      <div
        className={`section-header completed-header${completedCollapsed ? '' : ' open'}`}
        role="button" tabIndex={0} aria-expanded={!completedCollapsed}
        onClick={onToggleCompleted}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onToggleCompleted() } }}
      >
        <Icon name="chevronRight" size={16} className="completed-toggle" />
        <span className="section-title">Recently completed</span>
        {!completedCollapsed && <span className="section-count completed-count">{displayedCompletedWorkflows.length}</span>}
        <button
          className="icon-btn completed-refresh-btn"
          onClick={e => { e.stopPropagation(); onRefreshCompleted() }}
          disabled={completedLoading}
          title="Refresh completed workflows" aria-label="Refresh completed workflows"
        >{completedLoading ? '…' : <Icon name="refresh" size={16} />}</button>
      </div>
      {!completedCollapsed && (
        <div className="seg completed-filters" role="group" aria-label="Status" onClick={e => e.stopPropagation()}>
          {(['all', 'failed', 'completed', 'canceled', 'terminated'] as CompletedStatusFilter[]).map(f => (
            <button
              key={f}
              className={`seg-btn completed-filter-btn${completedStatusFilter === f ? ' active' : ''}`}
              onClick={() => setAndPersistFilter(f)}
            >
              {f === 'all' ? 'All' : f.charAt(0).toUpperCase() + f.slice(1)}
              {f === 'failed' && hasFailedWorkflows && <span className="completed-filter-warn" aria-label="some failed" />}
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
              const shortId = shortWorkflowId(wf.workflowId)
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
                  <div className="wf-meta">
                    <span className={`pill wf-status-badge wf-status-${statusCls}`}>{statusCls}</span>
                    <span className="pill wf-ns">{wf.namespace}</span>
                    <WorkflowRefs wf={wf} />
                    <span className="wf-age">
                      {wf.closeTime ? <span className="wf-close-time" title={new Date(wf.closeTime).toLocaleString()}>closed {heartbeatAge(wf.closeTime)}</span> : heartbeatAge(wf.startTime)}
                    </span>
                  </div>
                  <div className="wf-id" title={wf.workflowId}>
                    {url
                      ? <a href={url} target="_blank" rel="noreferrer" className="wf-link" onClick={e => e.stopPropagation()}>{shortId}</a>
                      : shortId}
                  </div>
                  <div className="wf-actions">
                    {(() => {
                      const ws = wfActionState(wf)
                      const wfMsg = wfActionMsg[wfKey(wf)] ?? ''
                      return (
                        <>
                          {ws === 'idle' && (
                            <button
                              className="btn btn-s btn-quiet wf-btn wf-btn-restart"
                              onClick={e => { e.stopPropagation(); handleWfActionLocal(wf, 'restart') }}
                            ><Icon name="restart" size={15} />Restart</button>
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

      <WorkflowHistoryPanel actions={actions} />

      {/* ── Confirm bar ── */}
      {pendingConfirm && (
        <ConfirmBar pending={pendingConfirm} onCancel={() => setPendingConfirm(null)} />
      )}
    </div>
  )
}
