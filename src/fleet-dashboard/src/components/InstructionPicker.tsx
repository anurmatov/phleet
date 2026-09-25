import type { InstructionSummary } from '../types'
import { instructionRows, type InstructionAssignment } from '../instructionAssignments'
import { sizeCell, type MeterLimit } from '../sizeMeter'
import SizeCellView from './SizeCellView'

interface InstructionPickerProps {
  allInstructions: InstructionSummary[]
  selected: InstructionAssignment[]
  /** The instruction soft limit from the server: null = disabled, undefined (or omitted) = unavailable. */
  sizeLimit?: MeterLimit
  onChange: (selected: InstructionAssignment[]) => void
}

export default function InstructionPicker({ allInstructions, selected, sizeLimit, onChange }: InstructionPickerProps) {
  // Excludes 'base' (auto-attached) and inactive instructions
  const rows = instructionRows(allInstructions, selected)

  function toggle(name: string) {
    if (selected.some(s => s.name === name)) {
      onChange(selected.filter(s => s.name !== name))
    } else {
      const nextOrder = selected.length > 0 ? Math.max(...selected.map(s => s.loadOrder)) + 1 : 1
      onChange([...selected, { name, loadOrder: nextOrder }])
    }
  }

  function setOrder(name: string, order: number) {
    onChange(selected.map(s => s.name === name ? { ...s, loadOrder: order } : s))
  }

  if (rows.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--muted)', padding: '4px 0' }}>
        No instructions available (create one in the Instructions panel).
      </div>
    )
  }

  return (
    <div className="instruction-picker">
      <table className="project-table">
        <colgroup>
          <col className="project-table-col-assigned" />
          <col />
          <col className="project-table-col-order" />
          <col className="project-table-col-size" />
        </colgroup>
        <thead>
          <tr>
            <th title="Assigned">✓</th>
            <th>Instruction</th>
            <th>Order</th>
            <th title="UTF-8 bytes of the current version, loaded in full on every turn">Size</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => {
            const id = `instruction-assign-${i}`
            return (
              <tr key={row.name} className={row.checked ? undefined : 'project-table-unassigned'}>
                <td>
                  <input id={id} type="checkbox" checked={row.checked} onChange={() => toggle(row.name)} />
                </td>
                <td className="project-table-name"><label htmlFor={id}>{row.name}</label></td>
                <td>
                  {row.checked && (
                    <input
                      type="number"
                      className="config-input config-input-short"
                      value={row.loadOrder}
                      min={1}
                      title="Load order"
                      aria-label={`Load order for ${row.name}`}
                      style={{ width: 56 }}
                      onChange={e => setOrder(row.name, parseInt(e.target.value, 10) || 1)}
                    />
                  )}
                </td>
                <td><SizeCellView cell={sizeCell(row.currentBytes, sizeLimit)} /></td>
              </tr>
            )
          })}
        </tbody>
      </table>
      <div className="instruction-picker-hint">
        <code>base</code> is auto-attached to every agent — it is not shown here.
        Load order controls the sequence in which instructions are concatenated into the system prompt.
      </div>
    </div>
  )
}
