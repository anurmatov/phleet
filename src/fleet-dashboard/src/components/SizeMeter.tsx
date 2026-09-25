import { meterState, type MeterLimit } from '../sizeMeter'

interface SizeMeterProps {
  text: string
  limit: MeterLimit
  /** `prompt` for instructions and project contexts, `memory` for a memory's embedding input. */
  kind: 'prompt' | 'memory'
}

/**
 * Live UTF-8 byte count under an editor (#346). Advisory only: it never disables Save or Create.
 */
export default function SizeMeter({ text, limit, kind }: SizeMeterProps) {
  const state = meterState(text, limit, kind === 'prompt' ? 'soft limit off' : 'guidance off')
  const overText = kind === 'prompt'
    ? 'over the soft limit — saving is still allowed'
    : 'over the embedding-input guidance — saving is still allowed'

  return (
    <div className={`size-meter size-meter-${state.status}`}>
      <span className="size-meter-count">{state.label}</span>
      {state.status === 'over' && <span className="size-meter-over">⚠ {overText}</span>}
    </div>
  )
}
