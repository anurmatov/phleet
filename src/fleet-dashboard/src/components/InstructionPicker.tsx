import type { InstructionSummary } from '../types'

interface InstructionAssignment {
  name: string
  loadOrder: number
}

interface InstructionPickerProps {
  allInstructions: InstructionSummary[]
  selected: InstructionAssignment[]
  onChange: (selected: InstructionAssignment[]) => void
}

export default function InstructionPicker({ allInstructions, selected, onChange }: InstructionPickerProps) {
  // Exclude 'base' (auto-attached) and inactive instructions
  const eligible = allInstructions.filter(i => i.isActive && i.name !== 'base')

  function isSelected(name: string) {
    return selected.some(s => s.name === name)
  }

  function getLoadOrder(name: string) {
    return selected.find(s => s.name === name)?.loadOrder ?? 1
  }

  function toggle(name: string) {
    if (isSelected(name)) {
      onChange(selected.filter(s => s.name !== name))
    } else {
      const nextOrder = selected.length > 0 ? Math.max(...selected.map(s => s.loadOrder)) + 1 : 1
      onChange([...selected, { name, loadOrder: nextOrder }])
    }
  }

  function setOrder(name: string, order: number) {
    onChange(selected.map(s => s.name === name ? { ...s, loadOrder: order } : s))
  }

  if (eligible.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--muted)', padding: '4px 0' }}>
        No instructions available (create one in the Instructions panel).
      </div>
    )
  }

  return (
    <div className="instruction-picker">
      {eligible.map(instr => {
        const checked = isSelected(instr.name)
        return (
          <div key={instr.name} className="instruction-picker-row">
            <label className="instruction-picker-label">
              <input
                type="checkbox"
                checked={checked}
                onChange={() => toggle(instr.name)}
              />
              <span>{instr.name}</span>
            </label>
            {checked && (
              <input
                type="number"
                className="config-input config-input-short"
                value={getLoadOrder(instr.name)}
                min={1}
                title="Load order"
                style={{ width: 56, marginLeft: 8 }}
                onChange={e => setOrder(instr.name, parseInt(e.target.value, 10) || 1)}
              />
            )}
          </div>
        )
      })}
      <div className="instruction-picker-hint">
        <code>base</code> is auto-attached to every agent — it is not shown here.
        Load order controls the sequence in which instructions are concatenated into the system prompt.
      </div>
    </div>
  )
}
