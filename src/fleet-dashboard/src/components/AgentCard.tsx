import { useState, useRef, useEffect } from 'react'
import type {
  AgentState,
  TaskRecord,
  RestartState,
  ReprovisionState,
  StopStartState,
  CancelState,
  WorkflowSummary,
} from '../types'
import Icon from './Icon'
import { statusDot, heartbeatAge, formatDuration, formatTime, formatUptime, temporalUiUrl } from '../utils'

interface AgentCardProps {
  agent: AgentState
  expanded: boolean
  onExpand: () => void
  taskHistory: TaskRecord[]
  historyLoading: boolean
  restartState: RestartState
  restartMsg: string
  reprovisionState: ReprovisionState
  reprovisionMsg: string
  stopState: StopStartState
  stopMsg: string
  startState: StopStartState
  startMsg: string
  cancelState: CancelState
  cancelMsg: string
  deleteState: 'idle' | 'confirming' | 'deleting' | 'success' | 'error'
  deleteMsg: string
  bgCancelStates: Record<string, 'idle' | 'cancelling' | 'done' | 'error'>
  expandedTasks: Set<string>
  copiedContainer: string | null
  isEditingConfig: boolean
  logViewerOpen: boolean
  workflowByAgent: Record<string, string>
  workflows: WorkflowSummary[]
  onRestart: () => void
  onRestartConfirm: () => void
  onRestartCancel: () => void
  onReprovision: () => void
  onReprovisionConfirm: () => void
  onReprovisionCancel: () => void
  onStop: () => void
  onStart: () => void
  onCancel: () => void
  onCancelConfirm: () => void
  onCancelCancel: () => void
  onDeleteClick: () => void
  onDeleteConfirm: () => void
  onDeleteCancel: () => void
  onBgCancel: (taskId: string) => void
  onViewLogs: () => void
  onEditConfig: () => void
  onToggleTask: (key: string) => void
  onCopyContainer: (name: string) => void
  highlightedEntityId: string | null
  onClearHighlight: () => void
}

export function PriorityBadge({ priority }: { priority?: boolean }) {
  return priority ? <span className="pill pill-purple queue-badge" title="primary-human queue lane">priority</span> : null
}

export default function AgentCard({
  agent, expanded, onExpand,
  taskHistory, historyLoading,
  restartState: rs, restartMsg: msg,
  reprovisionState: rps, reprovisionMsg: rpsmsg,
  stopState: ss, stopMsg: ssmsg,
  startState: sts, startMsg: stsmsg,
  cancelState: cs, cancelMsg: csmsg,
  deleteState, deleteMsg: dmsg,
  bgCancelStates,
  expandedTasks,
  copiedContainer,
  isEditingConfig,
  logViewerOpen,
  workflowByAgent,
  workflows,
  onRestart, onRestartConfirm, onRestartCancel,
  onReprovision, onReprovisionConfirm, onReprovisionCancel,
  onStop, onStart,
  onCancel, onCancelConfirm, onCancelCancel,
  onDeleteClick, onDeleteConfirm, onDeleteCancel,
  onBgCancel,
  onViewLogs,
  onEditConfig,
  onToggleTask,
  onCopyContainer,
  highlightedEntityId,
  onClearHighlight,
}: AgentCardProps) {
  const [overflowOpen, setOverflowOpen] = useState(false)
  const [historyExpanded, setHistoryExpanded] = useState(false)
  const isDead = agent.effectiveStatus === 'dead'
  const cardRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (highlightedEntityId === agent.agentName && cardRef.current) {
      cardRef.current.scrollIntoView({ behavior: 'smooth', block: 'center' })
      cardRef.current.classList.add('highlight-flash')
      const timer = setTimeout(() => {
        cardRef.current?.classList.remove('highlight-flash')
        onClearHighlight()
      }, 1500)
      return () => clearTimeout(timer)
    }
  }, [highlightedEntityId, agent.agentName, onClearHighlight])

  const busy = '…'
  const statusLabel = agent.effectiveStatus === 'dead' ? 'offline' : agent.effectiveStatus

  return (
    <div ref={cardRef} className={`agent-row status-${agent.effectiveStatus}${expanded ? ' expanded' : ''}${isEditingConfig ? ' config-editing' : ''}`}>

      {/* ── Main row ── */}
      <div className="agent-row-main">
        {/* The whole summary opens the row; the actions beside it are their own controls. */}
        <div
          className="agent-row-summary"
          role="button"
          tabIndex={0}
          aria-expanded={expanded}
          onClick={onExpand}
          onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onExpand() } }}
        >
          <span className="agent-row-dot" title={`${statusLabel} · ${heartbeatAge(agent.lastSeen)}`}>{statusDot(agent.effectiveStatus)}</span>
          <span className="agent-row-id">
            <span className="agent-row-name">{agent.agentName}</span>
            {agent.model && <span className="agent-model agent-row-model">{agent.model}</span>}
          </span>
          <span className="agent-row-task">
            {agent.currentTask
              ? <span className="task-active task-truncated">{agent.currentTask}</span>
              : <span className="task-idle">idle</span>
            }
          </span>
          <span className="agent-row-badges">
            {agent.queuedCount > 0 && (
              <span className="pill pill-info agent-row-queue-badge">{agent.queuedCount} queued</span>
            )}
            {(agent.priorityQueuedCount ?? 0) > 0 && (
              <span className="pill pill-purple agent-row-queue-badge">{agent.priorityQueuedCount} priority</span>
            )}
            {agent.backgroundTasks && agent.backgroundTasks.length > 0 && (
              <span className="pill agent-row-bg-badge">{agent.backgroundTasks.length} bg</span>
            )}
            {agent.effectiveStatus === 'stale' && (
              <span className="pill pill-warn agent-row-hb hb-stale" title={heartbeatAge(agent.lastSeen)}>stale</span>
            )}
            {agent.effectiveStatus === 'dead' && (
              <span className="pill pill-danger agent-row-hb hb-dead" title={heartbeatAge(agent.lastSeen)}>offline</span>
            )}
          </span>
        </div>

        {/* The actions used most, one press away. On a phone they move into the opened row, with labels. */}
        <div className="agent-row-actions" onClick={e => e.stopPropagation()}>
          <button
            className={`icon-btn${rs === 'confirming' ? ' confirming' : ''}`}
            title="Restart" aria-label="Restart"
            onClick={onRestart}
            disabled={isDead || rs === 'restarting'}
          >{rs === 'restarting' ? busy : <Icon name="restart" />}</button>
          <button
            className={`icon-btn${rps === 'confirming' ? ' confirming' : ''}`}
            title="Reprovision" aria-label="Reprovision"
            onClick={onReprovision}
            disabled={isDead || rps === 'provisioning'}
          >{rps === 'provisioning' ? busy : <Icon name="reprovision" />}</button>
          <button
            className={`icon-btn${cs === 'confirming' ? ' confirming' : ''}`}
            title="Cancel task" aria-label="Cancel task"
            onClick={onCancel}
            disabled={!agent.currentTask || cs === 'cancelling'}
          >{cs === 'cancelling' ? busy : <Icon name="cancel" />}</button>
          <button
            className={`icon-btn${logViewerOpen ? ' active' : ''}`}
            title="Logs" aria-label="Logs"
            onClick={onViewLogs}
          ><Icon name="logs" /></button>
          <button
            className={`icon-btn${overflowOpen ? ' active' : ''}`}
            title="More actions" aria-label="More actions"
            aria-expanded={overflowOpen}
            onClick={() => setOverflowOpen(o => !o)}
          ><Icon name="more" /></button>
        </div>

        {/* Overflow menu */}
        {overflowOpen && (
          <div className="menu agent-row-overflow" onClick={e => e.stopPropagation()}>
            <button className="menu-item" onClick={() => { onEditConfig(); setOverflowOpen(false) }}>
              <Icon name="settings" size={16} />{isEditingConfig ? 'Close config' : 'Edit config'}
            </button>
            {isDead
              ? <button className="menu-item" onClick={() => { onStart(); setOverflowOpen(false) }} disabled={sts === 'pending'}>
                  <Icon name="play" size={16} />{sts === 'pending' ? busy : 'Start'}
                </button>
              : <button className={`menu-item${ss === 'confirming' ? ' menu-item-danger' : ''}`} onClick={() => { onStop(); setOverflowOpen(false) }}>
                  <Icon name="stop" size={16} />{ss === 'confirming' ? 'Confirm stop?' : 'Stop'}
                </button>
            }
            <button
              className="menu-item menu-item-danger overflow-delete"
              onClick={() => { onDeleteClick(); setOverflowOpen(false) }}
              disabled={deleteState === 'deleting'}
            ><Icon name="trash" size={16} />{deleteState === 'deleting' ? busy : 'Delete'}</button>
          </div>
        )}

        {/* Expand chevron */}
        <button className="icon-btn agent-row-chevron" onClick={onExpand} title={expanded ? 'Collapse' : 'Expand'} aria-label={expanded ? 'Collapse' : 'Expand'}>
          <Icon name="chevronDown" />
        </button>
      </div>

      {/* ── Inline confirmations ── */}
      {rs === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Restart <strong>{agent.agentName}</strong>? This will interrupt any running task.</span>
          <div className="row-confirm-actions">
            <button className="btn btn-s row-confirm-cancel" onClick={onRestartCancel}>Cancel</button>
            <button className="btn btn-s btn-primary row-confirm-ok" onClick={onRestartConfirm}>Restart</button>
          </div>
        </div>
      )}
      {rps === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Reprovision <strong>{agent.agentName}</strong>? Container will be recreated.</span>
          <div className="row-confirm-actions">
            <button className="btn btn-s row-confirm-cancel" onClick={onReprovisionCancel}>Cancel</button>
            <button className="btn btn-s btn-primary row-confirm-ok" onClick={onReprovisionConfirm}>Reprovision</button>
          </div>
        </div>
      )}
      {cs === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Cancel all tasks on <strong>{agent.agentName}</strong>?</span>
          <div className="row-confirm-actions">
            <button className="btn btn-s row-confirm-cancel" onClick={onCancelCancel}>Keep Running</button>
            <button className="btn btn-s btn-danger row-confirm-ok row-confirm-cancel-ok" onClick={onCancelConfirm}>Cancel Tasks</button>
          </div>
        </div>
      )}
      {deleteState === 'confirming' && (
        <div className="row-confirm row-confirm-danger" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Delete <strong>{agent.agentName}</strong>? Container and DB record will be removed.</span>
          <div className="row-confirm-actions">
            <button className="btn btn-s row-confirm-cancel" onClick={onDeleteCancel}>Cancel</button>
            <button className="btn btn-s btn-danger row-confirm-ok row-confirm-delete-ok" onClick={onDeleteConfirm}>Delete</button>
          </div>
        </div>
      )}

      {/* ── Feedback messages ── */}
      {(rs === 'success' || rs === 'error') && (
        <div className={`row-feedback row-feedback-${rs}`}>{msg}</div>
      )}
      {(rps === 'success' || rps === 'error') && (
        <div className={`row-feedback row-feedback-${rps}`}>{rpsmsg}</div>
      )}
      {(ss === 'success' || ss === 'error') && (
        <div className={`row-feedback row-feedback-${ss}`}>{ssmsg}</div>
      )}
      {(sts === 'success' || sts === 'error') && (
        <div className={`row-feedback row-feedback-${sts}`}>{stsmsg}</div>
      )}
      {(cs === 'success' || cs === 'error') && (
        <div className={`row-feedback row-feedback-${cs}`}>{csmsg}</div>
      )}
      {(deleteState === 'success' || deleteState === 'error') && (
        <div className={`row-feedback row-feedback-${deleteState}`}>{dmsg}</div>
      )}

      {/* ── Expanded detail panel ── */}
      {expanded && (
        <div className="agent-row-detail" onClick={e => e.stopPropagation()}>
          {isEditingConfig && (
            <div className="row-detail-item">
              <span className="config-editing-indicator">editing config…</span>
            </div>
          )}

          {/* Current task (expanded view) */}
          {agent.currentTask && (
            <div className="row-detail-item">
              <span className="row-detail-label">task</span>
              <span
                className={`task-text task-active${expandedTasks.has(agent.agentName) ? ' task-expanded' : ''}`}
                onClick={() => onToggleTask(agent.agentName)}
                title="Click to expand/collapse"
                style={{ cursor: 'pointer' }}
              >{agent.currentTask}</span>
            </div>
          )}

          {agent.currentTaskId && (
            <div className="row-detail-item">
              <span className="row-detail-label">task id</span>
              <span className="row-detail-value">{agent.currentTaskId}</span>
            </div>
          )}

          {/* Workflow link */}
          {workflowByAgent[agent.agentName] && (() => {
            const wfId = workflowByAgent[agent.agentName]
            const wf = workflows.find(w => w.workflowId === wfId)
            const url = wf ? temporalUiUrl(wf) : null
            const label = wf ? wf.workflowType : wfId
            return (
              <div className="row-detail-item">
                <span className="row-detail-label">workflow</span>
                <span className="row-detail-value">
                  {url ? <a href={url} target="_blank" rel="noreferrer" className="wf-link">{label}</a> : label}
                </span>
              </div>
            )
          })()}

          {/* Uptime */}
          {formatUptime(agent.containerStartedAt) && (
            <div className="row-detail-item">
              <span className="row-detail-label">uptime</span>
              <span className="row-detail-value">{formatUptime(agent.containerStartedAt)}</span>
            </div>
          )}

          {/* Container name */}
          {agent.containerName && (
            <div className="row-detail-item">
              <span className="row-detail-label">container</span>
              <span
                className={`row-detail-value container-id${copiedContainer === agent.containerName ? ' container-id-copied' : ''}`}
                title={copiedContainer === agent.containerName ? 'copied!' : 'click to copy'}
                onClick={() => onCopyContainer(agent.containerName!)}
                style={{ cursor: 'pointer' }}
              >
                {copiedContainer === agent.containerName ? 'copied!' : agent.containerName}
              </span>
            </div>
          )}

          <div className="row-detail-item">
            <span className="row-detail-label">journal</span>
            <span className="row-detail-value">
              {agent.journal
                ? `spool ${agent.journal.spoolDepth} · oldest ${agent.journal.oldestAgeSeconds == null ? 'n/a' : `${agent.journal.oldestAgeSeconds}s`} · auth ${agent.journal.authFailed !== 0 ? 'failed' : 'ok'} · binding ${agent.journal.bindingFailed == null ? 'n/a' : agent.journal.bindingFailed !== 0 ? 'failed' : 'ok'} · dropped ${agent.journal.dropped} · dead ${agent.journal.dead}`
                : 'n/a'}
            </span>
          </div>

          {/* Queued messages */}
          {agent.queuedCount > 0 && (
            <div className="row-detail-item">
              <span className="row-detail-label">queued</span>
              <div className="row-detail-queue">
                {agent.queuedMessages && agent.queuedMessages.map((q, i) => (
                  <div key={i} className="queue-item">
                    <span className="queue-source">{q.source}</span>
                    <PriorityBadge priority={q.priority} />
                    <span className="queue-preview">{q.preview}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Background tasks */}
          {agent.backgroundTasks && agent.backgroundTasks.length > 0 && (
            <div className="row-detail-bg">
              <div className="bg-tasks-label">background tasks ({agent.backgroundTasks.length})</div>
              {agent.backgroundTasks.map(bt => {
                const bgKey = `${agent.agentName}/${bt.taskId}`
                const bgState = bgCancelStates[bgKey] ?? 'idle'
                return (
                  <div key={bt.taskId} className="bg-task-item">
                    <span className="bg-task-type">{bt.taskType}</span>
                    <span className="bg-task-desc">{bt.description}</span>
                    <span className="bg-task-elapsed">{bt.elapsedSeconds}s</span>
                    <button
                      className="icon-btn icon-btn-s bg-task-cancel-btn"
                      title="Cancel background task" aria-label="Cancel background task"
                      disabled={bgState === 'cancelling'}
                      onClick={() => onBgCancel(bt.taskId)}
                    >
                      {bgState === 'cancelling' ? '…' : <Icon name={bgState === 'done' ? 'check' : 'close'} size={14} />}
                    </button>
                    {bt.summary && <div className="bg-task-summary">{bt.summary}</div>}
                  </div>
                )
              })}
            </div>
          )}

          {/* The same actions with their names, for a phone, where the row has no room for them. */}
          <div className="agent-row-detail-actions">
            <button className="btn btn-s" title="Logs" onClick={onViewLogs}><Icon name="logs" size={16} />Logs</button>
            <button className="btn btn-s" onClick={onEditConfig}>
              <Icon name="settings" size={16} />{isEditingConfig ? 'Close config' : 'Edit config'}
            </button>
            <button className="btn btn-s" title="Restart" onClick={onRestart} disabled={isDead || rs === 'restarting'}>
              <Icon name="restart" size={16} />{rs === 'restarting' ? busy : 'Restart'}
            </button>
            <button className="btn btn-s" title="Reprovision" onClick={onReprovision} disabled={isDead || rps === 'provisioning'}>
              <Icon name="reprovision" size={16} />{rps === 'provisioning' ? busy : 'Reprovision'}
            </button>
            <button className="btn btn-s" title="Cancel task" onClick={onCancel} disabled={!agent.currentTask || cs === 'cancelling'}>
              <Icon name="cancel" size={16} />{cs === 'cancelling' ? busy : 'Cancel task'}
            </button>
            {isDead
              ? <button className="btn btn-s" onClick={onStart} disabled={sts === 'pending'}>
                  <Icon name="play" size={16} />{sts === 'pending' ? busy : 'Start'}
                </button>
              : <button className={`btn btn-s${ss === 'confirming' ? ' btn-danger' : ''}`} onClick={onStop}>
                  <Icon name="stop" size={16} />{ss === 'confirming' ? 'Confirm stop?' : 'Stop'}
                </button>
            }
            <button className="btn btn-s btn-danger-quiet" onClick={onDeleteClick} disabled={deleteState === 'deleting'}>
              <Icon name="trash" size={16} />{deleteState === 'deleting' ? busy : 'Delete'}
            </button>
          </div>

          {/* Task history */}
          <div className="task-history">
            <div className="history-title">Recent Tasks</div>
            {historyLoading && <div className="history-empty">Loading…</div>}
            {!historyLoading && taskHistory.length === 0 && (
              <div className="history-empty">No completed tasks yet.</div>
            )}
            {!historyLoading && (historyExpanded ? taskHistory : taskHistory.slice(0, 1)).map((t, i) => {
              const historyKey = `${agent.agentName}-history-${i}`
              return (
                <div key={i} className="history-item">
                  <div
                    className={`history-item-text${expandedTasks.has(historyKey) ? ' task-expanded' : ''}`}
                    onClick={() => onToggleTask(historyKey)}
                    title="Click to expand/collapse"
                    style={{ cursor: 'pointer' }}
                  >{t.taskText}</div>
                  <div className="history-item-meta">
                    <span>{formatTime(t.startedAt)}</span>
                    <span className="history-duration">{formatDuration(t.durationSeconds)}</span>
                  </div>
                </div>
              )
            })}
            {!historyLoading && taskHistory.length > 1 && (
              <button className="btn btn-s btn-quiet history-show-more" onClick={() => setHistoryExpanded(e => !e)}>
                {historyExpanded ? 'show less' : `+${taskHistory.length - 1} more`}
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
