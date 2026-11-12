import { useEffect, useState } from 'react'
import type { SignalModalState, WorkflowEvent } from '../types'
import { apiFetch } from '../utils'
import { humanInputChoices, validHumanAnswer, humanInputHistoryUrl, humanInputIdentity, humanInputRequestFromHistory, type HumanInputRequest } from '../humanInputRequest'

interface SignalModalProps {
  modal: SignalModalState
  currentGate?: boolean
  onClose: () => void
  onCommentChange: (comment: string) => void
  onSend: (payload?: string) => void
}

export default function SignalModal({ modal, currentGate = true, onClose, onCommentChange, onSend }: SignalModalProps) {
  const approval = modal.signalName === 'request-input'
  const identity = humanInputIdentity(modal.wf)
  const [answer, setAnswer] = useState<{ identity: string; value: string }>({ identity, value: '' })
  const value = answer.identity === identity ? answer.value : ''
  const [loaded, setLoaded] = useState<{ identity: string; request?: HumanInputRequest; error?: string } | null>(null)
  useEffect(() => {
    if (!approval) return
    if (!currentGate) {
      setLoaded({ identity, error: 'This request changed or closed. Refresh before deciding.' })
      return
    }
    if (!modal.wf.runId) {
      setLoaded({ identity, error: 'Exact run is missing. Refresh before deciding.' })
      return
    }
    let active = true
    setLoaded(null)
    apiFetch(humanInputHistoryUrl(modal.wf))
      .then(async r => {
        if (!r.ok) throw new Error('Request history is unavailable. Refresh before deciding.')
        return r.json() as Promise<WorkflowEvent[]>
      })
      .then(events => {
        const request = humanInputRequestFromHistory(modal.wf, events)
        if (active) setLoaded({ identity, request })
      })
      .catch(() => { if (active) setLoaded({ identity, error: 'Cannot load the exact request. Refresh before deciding.' }) })
    return () => { active = false }
  }, [approval, identity, modal.wf, currentGate])
  const request = loaded?.identity === identity ? loaded.request : undefined
  const error = loaded?.identity === identity ? loaded.error : undefined
  // No token check here (#530): the page only loads signed in, and the orchestrator decides what a
  // signed-in browser may do.
  const blocked = approval && (!currentGate || !request || modal.comment.length > 2000 || !validHumanAnswer(request, value))
  return (
    <div className="signal-modal-backdrop" onClick={onClose}>
      <div className="signal-modal" role="dialog" aria-modal="true" aria-label="Confirm workflow decision" onClick={e => e.stopPropagation()}
        style={{ maxHeight: '90dvh', overflowY: 'auto', overflowWrap: 'anywhere', minWidth: 0 }}>
        <div className="signal-modal-title">
          Send signal: <strong>{modal.button.label}</strong>
          <span className="signal-modal-wf">→ {modal.wf.workflowType}</span>
        </div>
        {approval && <section aria-label="Exact human input request" aria-busy={!loaded}>
          <p>Request: {modal.wf.workflowId}</p>
          <p>Run: {modal.wf.runId}</p>
          {!request && <p role="status">{error ?? 'Loading exact request…'}</p>}
          {request && <>
            <p>Requester: {request.Origin.Kind === 'agent' ? request.Origin.RequesterAgent : `${request.Origin.Namespace}/${request.Origin.WorkflowId}/${request.Origin.RunId}`}</p>
            <strong>Question</strong><div style={{ whiteSpace: 'pre-wrap' }}>{request.Question}</div>
            <strong>Context</strong><div style={{ whiteSpace: 'pre-wrap' }}>{request.Context}</div>
            <p>Answer mode: {request.AnswerMode}</p>
            {request.AnswerMode === 'text' ? <textarea aria-label="Answer" value={value} maxLength={2000} rows={4}
              onChange={e => setAnswer({ identity, value: e.target.value })} /> :
              <fieldset><legend>Choose one answer</legend>{humanInputChoices(request).map(c => <label key={c.Id} style={{ display: 'block', whiteSpace: 'pre-wrap' }}>
                <input type="radio" name="human-answer" value={c.Id} checked={value === c.Id}
                  onChange={() => setAnswer({ identity, value: c.Id })} />{c.Label}</label>)}</fieldset>}
            <p>This records only this decision. It does not execute an action.</p>
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
          <button className="signal-modal-send" disabled={blocked} onClick={() => { if (!blocked) onSend(approval ? JSON.stringify({ Value: value }) : undefined) }}>Send</button>
          <button className="signal-modal-cancel" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  )
}
