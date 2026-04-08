import { useDraggable } from '@dnd-kit/core'
import { STEP_CATEGORIES, STEP_COLORS } from './editorTypes'
import type { StepType } from './editorTypes'

interface StepPaletteProps {
  onAddStep?: (type: StepType) => void
}

export default function StepPalette({ onAddStep }: StepPaletteProps) {
  return (
    <div className="wfe-palette">
      <div className="wfe-palette-title">Steps</div>
      {STEP_CATEGORIES.map(cat => (
        <div key={cat.label} className="wfe-palette-group">
          <div className="wfe-palette-group-label">{cat.label}</div>
          {cat.types.map(type => (
            <PaletteItem key={type} type={type} onAdd={onAddStep} />
          ))}
        </div>
      ))}
    </div>
  )
}

function PaletteItem({ type, onAdd }: { type: StepType; onAdd?: (t: StepType) => void }) {
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: `palette:${type}`,
    data: { source: 'palette', stepType: type },
  })

  return (
    <div
      ref={setNodeRef}
      className={`wfe-palette-item${isDragging ? ' dragging' : ''}`}
      style={{ opacity: isDragging ? 0.4 : 1 }}
      {...listeners}
      {...attributes}
      onClick={() => onAdd?.(type)}
      title={`Drag to add, or click to append`}
    >
      <span className="wfe-step-dot" style={{ background: STEP_COLORS[type] ?? '#94a3b8' }} />
      <span>{type}</span>
    </div>
  )
}
