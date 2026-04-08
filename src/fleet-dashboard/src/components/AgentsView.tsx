import type {
  AgentState,
  TaskRecord,
  RestartState,
  ReprovisionState,
  StopStartState,
  CancelState,
  WorkflowSummary,
  WorkflowEvent,
  SignalDef,
  SignalButton,
  WfActionState,
} from '../types'
import { temporalUiUrl, heartbeatAge, decodeUnicode } from '../utils'
import AgentCard from './AgentCard'

interface AgentsViewProps {
  sorted: AgentState[]
  expandedAgent: string | null
  taskHistory: Record<string, TaskRecord[]>
  historyLoading: Record<string, boolean>
  restartStates: Record<string, RestartState>
  restartMsg: Record<string, string>
  reprovisionStates: Record<string, ReprovisionState>
  reprovisionMsg: Record<string, string>
  stopStates: Record<string, StopStartState>
  stopMsg: Record<string, string>
  startStates: Record<string, StopStartState>
  startMsg: Record<string, string>
  cancelStates: Record<string, CancelState>
  cancelMsg: Record<string, string>
  deleteStates: Record<string, 'idle' | 'confirming' | 'deleting' | 'success' | 'error'>
  deleteMsg: Record<string, string>
  bgCancelStates: Record<string, 'idle' | 'cancelling' | 'done' | 'error'>
  expandedTasks: Set<string>
  copiedContainer: string | null
  configAgent: string | null
  logViewer: string | null
  workflowByAgent: Record<string, string>
  workflows: WorkflowSummary[]
  // Workflow section
  agentByWorkflow: Record<string, string>
  wfActionStates: Record<string, WfActionState>
  wfActionMsg: Record<string, string>
  signalStates: Record<string, 'idle' | 'pending' | 'success' | 'error'>
  signalMsg: Record<string, string>
  signalConfirm: string | null
  signalRegistry: Record<string, SignalDef[]>
  wfMenuOpen: Record<string, boolean>
  selectedWf: WorkflowSummary | null
  wfHistory: WorkflowEvent[]
  wfHistoryLoading: boolean
  wfHistoryError: string | null
  expandedEvents: Set<number>
  onExpandAgent: (agentName: string) => void
  onRestart: (agentName: string) => void
  onRestartConfirm: (agentName: string) => void
  onRestartCancel: (agentName: string) => void
  onReprovision: (agentName: string) => void
  onReprovisionConfirm: (agentName: string) => void
  onReprovisionCancel: (agentName: string) => void
  onStop: (agentName: string) => void
  onStart: (agentName: string) => void
  onCancel: (agentName: string) => void
  onCancelConfirm: (agentName: string) => void
  onCancelCancel: (agentName: string) => void
  onDeleteClick: (agentName: string) => void
  onDeleteConfirm: (agentName: string) => void
  onDeleteCancel: (agentName: string) => void
  onBgCancel: (agentName: string, taskId: string) => void
  onViewLogs: (agentName: string) => void
  onEditConfig: (agentName: string) => void
  onToggleTask: (key: string) => void
  onCopyContainer: (name: string) => void
  // Workflow handlers
  onWfClick: (wf: WorkflowSummary) => void
  onWfAction: (wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') => void
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
  signalConfirmKey: (wf: WorkflowSummary, sigName: string, btnLabel: string) => string
  highlightedEntityId: string | null
  onClearHighlight: () => void
}

export default function AgentsView({
  sorted,
  expandedAgent,
  taskHistory,
  historyLoading,
  restartStates,
  restartMsg,
  reprovisionStates,
  reprovisionMsg,
  stopStates,
  stopMsg,
  startStates,
  startMsg,
  cancelStates,
  cancelMsg,
  deleteStates,
  deleteMsg,
  bgCancelStates,
  expandedTasks,
  copiedContainer,
  configAgent,
  logViewer,
  workflowByAgent,
  workflows,
  agentByWorkflow,
  wfActionStates,
  wfActionMsg,
  signalStates,
  signalMsg,
  signalConfirm,
  wfMenuOpen,
  selectedWf,
  wfHistory,
  wfHistoryLoading,
  wfHistoryError,
  expandedEvents,
  onExpandAgent,
  onRestart,
  onRestartConfirm,
  onRestartCancel,
  onReprovision,
  onReprovisionConfirm,
  onReprovisionCancel,
  onStop,
  onStart,
  onCancel,
  onCancelConfirm,
  onCancelCancel,
  onDeleteClick,
  onDeleteConfirm,
  onDeleteCancel,
  onBgCancel,
  onViewLogs,
  onEditConfig,
  onToggleTask,
  onCopyContainer,
  onWfClick,
  onWfAction,
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
  signalConfirmKey,
  highlightedEntityId,
  onClearHighlight,
}: AgentsViewProps) {
  if (sorted.length === 0) {
    return (
      <div className="view-page">
        <div className="empty">No agents registered yet. Waiting for heartbeats…</div>
      </div>
    )
  }

  const activeWorkflows = workflows.filter(w =>
    w.status !== 'Completed' && w.status !== 'Failed' && w.status !== 'Canceled' && w.status !== 'Terminated' && w.status !== 'TimedOut'
  )

  return (
    <div className="view-page">
      {/* ── Agent list ── */}
      <div className="agent-list">
        {sorted.map(agent => (
          <AgentCard
            key={agent.agentName}
            agent={agent}
            expanded={expandedAgent === agent.agentName}
            onExpand={() => onExpandAgent(agent.agentName)}
            taskHistory={taskHistory[agent.agentName] ?? []}
            historyLoading={historyLoading[agent.agentName] ?? false}
            restartState={restartStates[agent.agentName] ?? 'idle'}
            restartMsg={restartMsg[agent.agentName] ?? ''}
            reprovisionState={reprovisionStates[agent.agentName] ?? 'idle'}
            reprovisionMsg={reprovisionMsg[agent.agentName] ?? ''}
            stopState={stopStates[agent.agentName] ?? 'idle'}
            stopMsg={stopMsg[agent.agentName] ?? ''}
            startState={startStates[agent.agentName] ?? 'idle'}
            startMsg={startMsg[agent.agentName] ?? ''}
            cancelState={cancelStates[agent.agentName] ?? 'idle'}
            cancelMsg={cancelMsg[agent.agentName] ?? ''}
            deleteState={deleteStates[agent.agentName] ?? 'idle'}
            deleteMsg={deleteMsg[agent.agentName] ?? ''}
            bgCancelStates={bgCancelStates}
            expandedTasks={expandedTasks}
            copiedContainer={copiedContainer}
            isEditingConfig={configAgent === agent.agentName}
            logViewerOpen={logViewer === agent.agentName}
            workflowByAgent={workflowByAgent}
            workflows={workflows}
            onRestart={() => onRestart(agent.agentName)}
            onRestartConfirm={() => onRestartConfirm(agent.agentName)}
            onRestartCancel={() => onRestartCancel(agent.agentName)}
            onReprovision={() => onReprovision(agent.agentName)}
            onReprovisionConfirm={() => onReprovisionConfirm(agent.agentName)}
            onReprovisionCancel={() => onReprovisionCancel(agent.agentName)}
            onStop={() => onStop(agent.agentName)}
            onStart={() => onStart(agent.agentName)}
            onCancel={() => onCancel(agent.agentName)}
            onCancelConfirm={() => onCancelConfirm(agent.agentName)}
            onCancelCancel={() => onCancelCancel(agent.agentName)}
            onDeleteClick={() => onDeleteClick(agent.agentName)}
            onDeleteConfirm={() => onDeleteConfirm(agent.agentName)}
            onDeleteCancel={() => onDeleteCancel(agent.agentName)}
            onBgCancel={(taskId) => onBgCancel(agent.agentName, taskId)}
            onViewLogs={() => onViewLogs(agent.agentName)}
            onEditConfig={() => onEditConfig(agent.agentName)}
            onToggleTask={onToggleTask}
            onCopyContainer={onCopyContainer}
            highlightedEntityId={highlightedEntityId}
            onClearHighlight={onClearHighlight}
          />
        ))}
      </div>

      {/* ── Active Workflows ── */}
      <div className="section-header" style={{ marginTop: 28 }}>
        <span className="section-title">Active Workflows</span>
        {activeWorkflows.length > 0 && <span className="section-count">{activeWorkflows.length}</span>}
      </div>

      {activeWorkflows.length === 0 ? (
        <div className="wf-empty">No running workflows.</div>
      ) : (
        <div className="wf-table">
          {activeWorkflows.map(wf => {
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
              <div key={`${wf.namespace}/${wf.workflowId}`} className={`wf-row ${wfClass}`}>
                {wfClass === 'wf-row-signal-waiting' && (
                  <span className="wf-signal-dot" title="Waiting for signal" />
                )}
                <div className="wf-type wf-type-clickable" onClick={() => onWfClick(wf)} title="Click to view execution history">{wf.workflowType}</div>
                <div className="wf-id" title={wf.workflowId}>
                  {url
                    ? <a href={url} target="_blank" rel="noreferrer" className="wf-link">{shortId}</a>
                    : shortId}
                </div>
                <div className="wf-meta">
                  <span className="wf-ns-badge">{wf.namespace}</span>
                  {sigDefs.length > 0 && (
                    <span className="wf-action-badge" title="This workflow may be awaiting input">action</span>
                  )}
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
                  {sigDefs.length > 0 && signalStates[signalKey(wf)] !== 'pending' && signalStates[signalKey(wf)] !== 'success' && signalStates[signalKey(wf)] !== 'error' && (
                    <div className="wf-signal-groups">
                      {sigDefs.map(sig => (
                        <div key={sig.name} className="wf-signal-group">
                          <span className="wf-signal-group-label">{sig.label}:</span>
                          <div className="wf-signal-btns">
                            {sig.buttons.map(btn => {
                              const ck = signalConfirmKey(wf, sig.name, btn.label)
                              const confirming = signalConfirm === ck
                              return (
                                <button
                                  key={`${sig.name}-${btn.label}`}
                                  className={`wf-btn wf-btn-signal wf-btn-signal-prominent${confirming ? ' confirming' : ''}`}
                                  title={confirming ? 'Click again to confirm' : `Send signal: ${sig.name}`}
                                  onClick={() => onSignalClick(wf, sig, btn)}
                                >
                                  {confirming ? `confirm ${btn.label.toLowerCase()}?` : btn.label}
                                </button>
                              )
                            })}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                  {signalStates[signalKey(wf)] === 'pending' && <span className="wf-action-feedback wf-action-pending">…</span>}
                  {signalStates[signalKey(wf)] === 'success' && <span className="wf-action-feedback wf-action-success">{signalMsg[signalKey(wf)]}</span>}
                  {signalStates[signalKey(wf)] === 'error' && <span className="wf-action-feedback wf-action-error">{signalMsg[signalKey(wf)]}</span>}

                  {(ws === 'idle' || ws === 'confirming-cancel' || ws === 'confirming-restart' || ws === 'confirming-terminate') && (
                    <div className="wf-secondary-actions">
                      <button
                        className="wf-btn wf-btn-more"
                        title="More actions"
                        onClick={() => onToggleMenu(k)}
                      >
                        {menuOpen ? '✕' : '···'}
                      </button>
                      {menuOpen && (
                        <div className="wf-more-menu">
                          {(ws === 'idle' || ws === 'confirming-cancel') && (
                            <button
                              className={`wf-btn wf-btn-cancel${ws === 'confirming-cancel' ? ' confirming' : ''}`}
                              onClick={() => onWfAction(wf, 'cancel')}
                            >
                              {ws === 'confirming-cancel' ? 'confirm cancel?' : 'cancel'}
                            </button>
                          )}
                          {(ws === 'idle' || ws === 'confirming-restart') && (
                            <button
                              className={`wf-btn wf-btn-restart${ws === 'confirming-restart' ? ' confirming' : ''}`}
                              onClick={() => onWfAction(wf, 'restart')}
                            >
                              {ws === 'confirming-restart' ? 'confirm restart?' : 'restart'}
                            </button>
                          )}
                          {(ws === 'idle' || ws === 'confirming-terminate') && (
                            <button
                              className={`wf-btn wf-btn-terminate${ws === 'confirming-terminate' ? ' confirming' : ''}`}
                              onClick={() => onWfAction(wf, 'terminate')}
                            >
                              {ws === 'confirming-terminate' ? 'confirm kill?' : 'kill'}
                            </button>
                          )}
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
    </div>
  )
}
