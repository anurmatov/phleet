import { useEffect, useState } from 'react'
import type { SignalModalState, WorkflowEvent } from '../types'
import { apiFetch, AUTH_TOKEN } from '../utils'
import { approvalHistoryUrl, approvalIdentity, approvalRequestFromHistory, type ApprovalRequest } from '../approvalRequest'

interface SignalModalProps {
  modal: SignalModalState
  onClose: () => void
  onCommentChange: (comment: string) => void
  onSend: () => void
}

export default function SignalModal({ modal, onClose, onCommentChange, onSend }: SignalModalProps) {
  const approval = modal.signalName === 'request-approval'
  const identity = approvalIdentity(modal.wf)
  const [loaded, setLoaded] = useState<{ identity: string; request?: ApprovalRequest; error?: string } | null>(null)
  useEffect(() => {
    if (!approval) return
    if (!modal.wf.runId) {
      setLoaded({ identity, error: 'Exact run is missing. Refresh before deciding.' })
      return
    }
    let active = true
    setLoaded(null)
    apiFetch(approvalHistoryUrl(modal.wf))
      .then(async r => {
        if (!r.ok) throw new Error('Request history is unavailable. Refresh before deciding.')
        return r.json() as Promise<WorkflowEvent[]>
      })
      .then(events => {
        const request = approvalRequestFromHistory(modal.wf, events)
        if (active) setLoaded({ identity, request })
      })
      .catch(() => { if (active) setLoaded({ identity, error: 'Cannot load the exact request. Refresh before deciding.' }) })
    return () => { active = false }
  }, [approval, identity, modal.wf])
  const request = loaded?.identity === identity ? loaded.request : undefined
  const error = loaded?.identity === identity ? loaded.error : undefined
  const blocked = approval && (!request || !AUTH_TOKEN || modal.comment.length > 2000)
  return (
    <div className="signal-modal-backdrop" onClick={onClose}>
      <div className="signal-modal" role="dialog" aria-modal="true" aria-label="Confirm workflow decision" onClick={e => e.stopPropagation()}
        style={{ maxHeight: '90dvh', overflowY: 'auto', overflowWrap: 'anywhere', minWidth: 0 }}>
        <div className="signal-modal-title">
          Send signal: <strong>{modal.button.label}</strong>
          <span className="signal-modal-wf">→ {modal.wf.workflowType}</span>
        </div>
        {approval && !AUTH_TOKEN && <p role="status">Read-only: an operator token is required to decide.</p>}
        {approval && <section aria-label="Exact approval request" aria-busy={!loaded}>
          <p>Request: {modal.wf.workflowId}</p>
          <p>Run: {modal.wf.runId}</p>
          {!request && <p role="status">{error ?? 'Loading exact request…'}</p>}
          {request && <>
            <p>Requester: {request.RequesterAgent}</p>
            <strong>Action</strong><div style={{ whiteSpace: 'pre-wrap' }}>{request.Action}</div>
            <strong>Scope</strong><div style={{ whiteSpace: 'pre-wrap' }}>{request.Scope}</div>
            <p>This records only this decision. It does not execute the action.</p>
          </>}
        </section>}
        <textarea
          className="signal-modal-input"
          aria-label="Comment (optional)"
          placeholder="Comment (optional)"
          value={modal.comment}
          onChange={e => onCommentChange(e.target.value)}
          maxLength={approval ? 2000 : undefined}
          rows={3}
          autoFocus
        />
        <div className="signal-modal-actions">
          <button className="signal-modal-send" disabled={blocked} onClick={() => { if (!blocked) onSend() }}>Send</button>
          <button className="signal-modal-cancel" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  )
}
