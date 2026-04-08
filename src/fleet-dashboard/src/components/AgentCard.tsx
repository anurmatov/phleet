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

  return (
    <div ref={cardRef} className={`agent-row status-${agent.effectiveStatus}${expanded ? ' expanded' : ''}${isEditingConfig ? ' config-editing' : ''}`}>

      {/* ── Main row ── */}
      <div className="agent-row-main">
        <div className="agent-row-stripe" />

        {/* Clickable summary area */}
        <div className="agent-row-summary" onClick={onExpand}>
          <span className="agent-row-dot">{statusDot(agent.effectiveStatus)}</span>
          <span className="agent-row-name">{agent.agentName}</span>
          {agent.model && <span className="agent-model agent-row-model">{agent.model}</span>}
          <span className="agent-row-task">
            {agent.currentTask
              ? <span className="task-active task-truncated">{agent.currentTask}</span>
              : <span className="task-idle">idle</span>
            }
          </span>
          {agent.queuedCount > 0 && (
            <span className="agent-row-queue-badge">{agent.queuedCount} queued</span>
          )}
          {agent.backgroundTasks && agent.backgroundTasks.length > 0 && (
            <span className="agent-row-bg-badge">{agent.backgroundTasks.length} bg</span>
          )}
          {agent.effectiveStatus === 'stale' && (
            <span className="agent-row-hb hb-stale" title={heartbeatAge(agent.lastSeen)}>stale</span>
          )}
          {agent.effectiveStatus === 'dead' && (
            <span className="agent-row-hb hb-dead" title={heartbeatAge(agent.lastSeen)}>offline</span>
          )}
        </div>

        {/* 5 inline action buttons */}
        <div className="agent-row-actions" onClick={e => e.stopPropagation()}>
          <button
            className={`row-btn${isDead ? ' row-btn-dim' : rs === 'confirming' ? ' confirming' : ''}`}
            title="Restart"
            onClick={onRestart}
            disabled={isDead || rs === 'restarting'}
          >{rs === 'restarting' ? '…' : '↺'}</button>
          <button
            className={`row-btn${isDead ? ' row-btn-dim' : rps === 'confirming' ? ' confirming' : ''}`}
            title="Reprovision"
            onClick={onReprovision}
            disabled={isDead || rps === 'provisioning'}
          >{rps === 'provisioning' ? '…' : '↻'}</button>
          <button
            className={`row-btn${!agent.currentTask ? ' row-btn-dim' : cs === 'confirming' ? ' confirming' : ''}`}
            title="Cancel task"
            onClick={onCancel}
            disabled={!agent.currentTask || cs === 'cancelling'}
          >{cs === 'cancelling' ? '…' : '✕'}</button>
          <button
            className={`row-btn${logViewerOpen ? ' active' : ''}`}
            title="Logs"
            onClick={onViewLogs}
          >≡</button>
          <button
            className={`row-btn${overflowOpen ? ' active' : ''}`}
            title="More actions"
            onClick={() => setOverflowOpen(o => !o)}
          >···</button>
        </div>

        {/* Overflow menu */}
        {overflowOpen && (
          <div className="agent-row-overflow" onClick={e => e.stopPropagation()}>
            <button onClick={() => { onEditConfig(); setOverflowOpen(false) }}>
              {isEditingConfig ? 'close config' : 'edit config'}
            </button>
            {isDead
              ? <button onClick={() => { onStart(); setOverflowOpen(false) }} disabled={sts === 'pending'}>
                  {sts === 'pending' ? '…' : 'start'}
                </button>
              : <button onClick={() => { onStop(); setOverflowOpen(false) }}>
                  {ss === 'confirming' ? 'confirm stop?' : 'stop'}
                </button>
            }
            <button
              className="overflow-delete"
              onClick={() => { onDeleteClick(); setOverflowOpen(false) }}
              disabled={deleteState === 'deleting'}
            >{deleteState === 'deleting' ? '…' : 'delete'}</button>
          </div>
        )}

        {/* Expand chevron */}
        <button className="agent-row-chevron" onClick={onExpand} title={expanded ? 'Collapse' : 'Expand'}>
          {expanded ? '▲' : '▼'}
        </button>
      </div>

      {/* ── Inline confirmations ── */}
      {rs === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Restart <strong>{agent.agentName}</strong>? This will interrupt any running task.</span>
          <div className="row-confirm-actions">
            <button className="row-confirm-ok" onClick={onRestartConfirm}>Restart</button>
            <button className="row-confirm-cancel" onClick={onRestartCancel}>Cancel</button>
          </div>
        </div>
      )}
      {rps === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Reprovision <strong>{agent.agentName}</strong>? Container will be recreated.</span>
          <div className="row-confirm-actions">
            <button className="row-confirm-ok" onClick={onReprovisionConfirm}>Reprovision</button>
            <button className="row-confirm-cancel" onClick={onReprovisionCancel}>Cancel</button>
          </div>
        </div>
      )}
      {cs === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Cancel all tasks on <strong>{agent.agentName}</strong>?</span>
          <div className="row-confirm-actions">
            <button className="row-confirm-ok row-confirm-cancel-ok" onClick={onCancelConfirm}>Cancel Tasks</button>
            <button className="row-confirm-cancel" onClick={onCancelCancel}>Keep Running</button>
          </div>
        </div>
      )}
      {deleteState === 'confirming' && (
        <div className="row-confirm" onClick={e => e.stopPropagation()}>
          <span className="row-confirm-text">Delete <strong>{agent.agentName}</strong>? Container and DB record will be removed.</span>
          <div className="row-confirm-actions">
            <button className="row-confirm-ok row-confirm-delete-ok" onClick={onDeleteConfirm}>Delete</button>
            <button className="row-confirm-cancel" onClick={onDeleteCancel}>Cancel</button>
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

          {/* Queued messages */}
          {agent.queuedCount > 0 && (
            <div className="row-detail-item">
              <span className="row-detail-label">queued</span>
              <div className="row-detail-queue">
                <span className="queue-badge">{agent.queuedCount}</span>
                {agent.queuedMessages && agent.queuedMessages.map((q, i) => (
                  <div key={i} className="queue-item">
                    <span className="queue-source">{q.source}</span>
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
                      className="bg-task-cancel-btn"
                      title="Cancel background task"
                      disabled={bgState === 'cancelling'}
                      onClick={() => onBgCancel(bt.taskId)}
                    >
                      {bgState === 'cancelling' ? '…' : bgState === 'done' ? '✓' : '✕'}
                    </button>
                    {bt.summary && <div className="bg-task-summary">{bt.summary}</div>}
                  </div>
                )
              })}
            </div>
          )}

          {/* Mobile-only action buttons (shown in drawer on mobile) */}
          <div className="agent-row-detail-actions">
            <button
              className={`row-btn${isDead ? ' row-btn-dim' : rs === 'confirming' ? ' confirming' : ''}`}
              title="Restart"
              onClick={onRestart}
              disabled={isDead || rs === 'restarting'}
            >{rs === 'restarting' ? '…' : '↺ restart'}</button>
            <button
              className={`row-btn${isDead ? ' row-btn-dim' : rps === 'confirming' ? ' confirming' : ''}`}
              title="Reprovision"
              onClick={onReprovision}
              disabled={isDead || rps === 'provisioning'}
            >{rps === 'provisioning' ? '…' : '↻ reprovision'}</button>
            <button
              className={`row-btn${!agent.currentTask ? ' row-btn-dim' : cs === 'confirming' ? ' confirming' : ''}`}
              title="Cancel task"
              onClick={onCancel}
              disabled={!agent.currentTask || cs === 'cancelling'}
            >{cs === 'cancelling' ? '…' : '✕ cancel'}</button>
            <button
              className={`row-btn${logViewerOpen ? ' active' : ''}`}
              title="Logs"
              onClick={onViewLogs}
            >≡ logs</button>
            <button onClick={onEditConfig} className="row-btn">
              {isEditingConfig ? '✎ close config' : '✎ edit config'}
            </button>
            {isDead
              ? <button className="row-btn" onClick={onStart} disabled={sts === 'pending'}>
                  {sts === 'pending' ? '…' : '▶ start'}
                </button>
              : <button className="row-btn" onClick={onStop}>
                  {ss === 'confirming' ? 'confirm stop?' : '■ stop'}
                </button>
            }
            <button
              className="row-btn overflow-delete"
              onClick={onDeleteClick}
              disabled={deleteState === 'deleting'}
            >{deleteState === 'deleting' ? '…' : '🗑 delete'}</button>
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
              <button className="history-show-more" onClick={() => setHistoryExpanded(e => !e)}>
                {historyExpanded ? 'show less' : `+${taskHistory.length - 1} more`}
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
