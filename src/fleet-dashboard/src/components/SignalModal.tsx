import type { SignalModalState } from '../types'

interface SignalModalProps {
  modal: SignalModalState
  onClose: () => void
  onCommentChange: (comment: string) => void
  onSend: () => void
}

export default function SignalModal({ modal, onClose, onCommentChange, onSend }: SignalModalProps) {
  return (
    <div className="signal-modal-backdrop" onClick={onClose}>
      <div className="signal-modal" onClick={e => e.stopPropagation()}>
        <div className="signal-modal-title">
          Send signal: <strong>{modal.button.label}</strong>
          <span className="signal-modal-wf">→ {modal.wf.workflowType}</span>
        </div>
        <textarea
          className="signal-modal-input"
          placeholder="Comment (optional)"
          value={modal.comment}
          onChange={e => onCommentChange(e.target.value)}
          rows={3}
          autoFocus
        />
        <div className="signal-modal-actions">
          <button className="signal-modal-send" onClick={onSend}>Send</button>
          <button className="signal-modal-cancel" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  )
}
