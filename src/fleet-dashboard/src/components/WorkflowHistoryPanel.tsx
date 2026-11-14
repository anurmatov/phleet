import type { WorkflowActions } from '../hooks/useWorkflowActions'
import { decodeUnicode } from '../utils'
import Modal from './Modal'

/**
 * A workflow's execution history, newest event first. Shown over whichever page lists the
 * workflow; it draws nothing until one is selected.
 */
export default function WorkflowHistoryPanel({ actions }: { actions: WorkflowActions }) {
  const { selectedWf, history, historyLoading, historyError, expandedEvents, toggleEvent, closeDetail, refreshDetail } = actions
  if (!selectedWf) return null

  // A completion event carries no agent; it is the one its activity was scheduled for.
  const agentByActivityType = new Map<string, string>()
  for (const e of history) {
    if (e.eventType === 'ActivityScheduled' && e.agent && e.activityType) agentByActivityType.set(e.activityType, e.agent)
  }

  return (
    <Modal label="Workflow history" overlayClassName="wf-detail-overlay" className="wf-detail-modal" onClose={closeDetail}>
      <div className="wf-detail-header">
        <span className="wf-detail-title">
          {selectedWf.workflowType} / <span className="wf-detail-id">{selectedWf.workflowId}</span>
        </span>
        <div className="wf-detail-header-actions">
          <button className="wf-detail-refresh" onClick={refreshDetail} disabled={historyLoading} title="Refresh history">↻</button>
          <button className="wf-detail-close" onClick={closeDetail}>✕</button>
        </div>
      </div>
      <div className="wf-detail-body">
        {historyLoading && <div className="wf-detail-loading">Loading history…</div>}
        {historyError && <div className="wf-detail-error">Error: {historyError}</div>}
        {!historyLoading && !historyError && history.length === 0 && (
          <div className="wf-detail-empty">No history events found.</div>
        )}
        {history.map(ev => {
          const agent = ev.agent ?? (ev.activityType ? agentByActivityType.get(ev.activityType) ?? null : null)
          const expanded = expandedEvents.has(ev.eventId)
          const hasDetail = !!(ev.inputSummary || ev.outputSummary || ev.failureMessage)
          return (
            <div key={ev.eventId} className={`wf-ev wf-ev-${ev.eventType.toLowerCase()}`}>
              <div
                className="wf-ev-header"
                onClick={hasDetail ? () => toggleEvent(ev.eventId) : undefined}
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
        })}
      </div>
    </Modal>
  )
}
