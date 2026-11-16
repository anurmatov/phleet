import { SESSION_CHOICE_HELP } from '../reprovision'
import FieldHint from './FieldHint'

/**
 * The one option of a reprovision question: keep the agent's session (unticked, the default) or
 * start a new one. The parent holds the answer and mounts this only while it is asking, so every
 * new question starts unticked.
 */
export default function SessionChoice({ fresh, onChange }: { fresh: boolean; onChange: (fresh: boolean) => void }) {
  return (
    <div className="session-choice">
      <label className="session-choice-label">
        <input type="checkbox" checked={fresh} onChange={e => onChange(e.target.checked)} />
        <span>New session</span>
      </label>
      <FieldHint>{SESSION_CHOICE_HELP}</FieldHint>
    </div>
  )
}
