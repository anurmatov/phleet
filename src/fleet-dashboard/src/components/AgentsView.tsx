import type { AgentState, WorkflowSummary } from '../types'
import type { AgentActions } from '../hooks/useAgentActions'
import { useTick } from '../hooks/useTick'
import type { WorkflowActions } from '../hooks/useWorkflowActions'
import { temporalUiUrl, heartbeatAge } from '../utils'
import AgentCard from './AgentCard'
import Icon from './Icon'
import { PhaseChip, WorkflowRefs, shortWorkflowId } from './WorkflowBits'
import WorkflowHistoryPanel from './WorkflowHistoryPanel'
import CommsJournalPanel from './CommsJournalPanel'

interface AgentsViewProps {
  sorted: AgentState[]
  /** Per-agent actions and what the page has open (hooks/useAgentActions.ts). */
  actions: AgentActions
  /** The agent whose config dialog is open, if any. */
  configAgent: string | null
  /** The agent whose log viewer is open, if any. */
  logViewer: string | null
  onViewLogs: (agentName: string) => void
  onEditConfig: (agentName: string) => void
  workflowByAgent: Record<string, string>
  workflows: WorkflowSummary[]
  agentByWorkflow: Record<string, string>
  /** Signals, cancel/restart/kill and the history panel (hooks/useWorkflowActions.ts). */
  workflowActions: WorkflowActions
  highlightedEntityId: string | null
  onClearHighlight: () => void
  onNewAgent: () => void
  /** What is still to be set up, shown under the title while there is anything. */
  banner?: React.ReactNode
}

export default function AgentsView({
  sorted, actions, configAgent, logViewer, onViewLogs, onEditConfig,
  workflowByAgent, workflows, agentByWorkflow, workflowActions,
  highlightedEntityId, onClearHighlight, onNewAgent, banner,
}: AgentsViewProps) {
  const {
    expandedAgent, taskHistory, historyLoading, expandedTasks, copiedContainer,
    restartStates, restartMsg, reprovisionStates, reprovisionMsg, stopStates, stopMsg, startStates, startMsg,
    cancelStates, cancelMsg, deleteStates, deleteMsg, bgCancelStates,
    toggleExpanded: onExpandAgent,
    restart: onRestart,
    restartConfirm: onRestartConfirm,
    restartCancel: onRestartCancel,
    reprovision: onReprovision,
    reprovisionConfirm: onReprovisionConfirm,
    reprovisionCancel: onReprovisionCancel,
    stop: onStop,
    start: onStart,
    cancel: onCancel,
    cancelConfirm: onCancelConfirm,
    cancelCancel: onCancelCancel,
    deleteClick: onDeleteClick,
    deleteConfirm: onDeleteConfirm,
    deleteCancel: onDeleteCancel,
    bgCancel: onBgCancel,
    toggleTask: onToggleTask,
    copyContainer: onCopyContainer,
  } = actions
  const {
    wfActionMsg, signalStates, signalMsg, signalConfirm,
    menuOpen: wfMenuOpen,
    openDetail: onWfClick,
    confirmAction: onWfAction,
    signalClick: onSignalClick,
    toggleMenu: onToggleMenu,
    getSignalDefs, wfActionState, signalKey, wfKey, wfStatusClass, signalConfirmKey,
  } = workflowActions
  // Agent uptimes, heartbeat ages and workflow ages are relative to now: redraw as the clock moves.
  useTick()
  const activeWorkflows = workflows.filter(w =>
    w.status !== 'Completed' && w.status !== 'Failed' && w.status !== 'Canceled' && w.status !== 'Terminated' && w.status !== 'TimedOut'
  )
  // What waits for a person goes first: it is the reason most visits to this page happen.
  const waiting = activeWorkflows.filter(w => getSignalDefs(w).length > 0)
  const orderedWorkflows = [...waiting, ...activeWorkflows.filter(w => getSignalDefs(w).length === 0)]

  const working = sorted.filter(a => a.effectiveStatus !== 'dead' && a.currentTask).length
  const offline = sorted.filter(a => a.effectiveStatus === 'dead').length
  const stale = sorted.filter(a => a.effectiveStatus === 'stale' && !a.currentTask).length
  const idle = sorted.length - working - offline - stale

  const header = (
    <div className="view-page-header">
      <h1 className="view-page-title">
        Agents
        {sorted.length > 0 && <span className="section-count">{sorted.length}</span>}
      </h1>
      {sorted.length > 0 && (
        <div className="fleet-summary" aria-label="Fleet summary">
          {working > 0 && <span className="pill pill-ok">{working} working</span>}
          {idle > 0 && <span className="pill">{idle} idle</span>}
          {stale > 0 && <span className="pill pill-warn">{stale} stale</span>}
          {offline > 0 && <span className="pill pill-danger">{offline} offline</span>}
        </div>
      )}
      {/* The sidebar carries this on a desktop; a phone has no sidebar on screen. */}
      <button className="btn btn-primary only-phone" onClick={onNewAgent}>
        <Icon name="plus" size={16} />New agent
      </button>
    </div>
  )

  if (sorted.length === 0) {
    return (
      <div className="view-page">
        {header}
        {banner}
        <div className="empty">No agents registered yet. Waiting for heartbeats…</div>
      </div>
    )
  }

  return (
    <div className="view-page">
      {header}
      {banner}
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

      {/* ── Running workflows ── */}
      <div className="section-header">
        <span className="section-title">Running workflows</span>
        {activeWorkflows.length > 0 && <span className="section-count">{activeWorkflows.length}</span>}
        {waiting.length > 0 && <span className="pill pill-warn">{waiting.length} waiting for you</span>}
      </div>

      {activeWorkflows.length === 0 ? (
        <div className="wf-empty">No running workflows.</div>
      ) : (
        <div className="wf-table">
          {orderedWorkflows.map(wf => {
            const url = temporalUiUrl(wf)
            const shortId = shortWorkflowId(wf.workflowId)
            const ws = wfActionState(wf)
            const wfMsg = wfActionMsg[wfKey(wf)] ?? ''
            const sigDefs = getSignalDefs(wf)
            const wfClass = wfStatusClass(wf, sigDefs)
            const k = wfKey(wf)
            const menuOpen = wfMenuOpen[k] ?? false

            return (
              <div key={`${wf.namespace}/${wf.workflowId}`} className={`wf-row ${wfClass}`}>
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
                                  className={`btn btn-s wf-btn wf-btn-signal ${confirming ? 'btn-danger confirming' : 'btn-primary'}`}
                                  title={confirming ? 'Click again to confirm' : `Send signal: ${sig.name}`}
                                  onClick={() => onSignalClick(wf, sig, btn)}
                                >
                                  {confirming ? `Confirm ${btn.label.toLowerCase()}?` : btn.label}
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
                        className={`icon-btn wf-btn-more${menuOpen ? ' active' : ''}`}
                        title="More actions" aria-label="More actions" aria-expanded={menuOpen}
                        onClick={() => onToggleMenu(k)}
                      >
                        <Icon name={menuOpen ? 'close' : 'more'} />
                      </button>
                      {menuOpen && (
                        <div className="menu wf-more-menu">
                          {(ws === 'idle' || ws === 'confirming-cancel') && (
                            <button
                              className={`menu-item${ws === 'confirming-cancel' ? ' menu-item-danger' : ''}`}
                              onClick={() => onWfAction(wf, 'cancel')}
                            >
                              {ws === 'confirming-cancel' ? 'Confirm cancel?' : 'Cancel'}
                            </button>
                          )}
                          {(ws === 'idle' || ws === 'confirming-restart') && (
                            <button
                              className={`menu-item${ws === 'confirming-restart' ? ' menu-item-danger' : ''}`}
                              onClick={() => onWfAction(wf, 'restart')}
                            >
                              {ws === 'confirming-restart' ? 'Confirm restart?' : 'Restart'}
                            </button>
                          )}
                          {(ws === 'idle' || ws === 'confirming-terminate') && (
                            <button
                              className={`menu-item${ws === 'confirming-terminate' ? ' menu-item-danger' : ''}`}
                              onClick={() => onWfAction(wf, 'terminate')}
                            >
                              {ws === 'confirming-terminate' ? 'Confirm kill?' : 'Kill'}
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

      <WorkflowHistoryPanel actions={workflowActions} />

      {/* How the message journal is doing: worth a glance now and then, never the first thing. */}
      <CommsJournalPanel />
    </div>
  )
}
