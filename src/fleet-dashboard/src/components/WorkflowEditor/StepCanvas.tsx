import { useState } from 'react'
import {
  DndContext,
  DragOverlay,
  closestCenter,
  PointerSensor,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import type { DragEndEvent, DragStartEvent } from '@dnd-kit/core'
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable'
import type { AnyStep, StepPath, StepType, ValidationError } from './editorTypes'
import { ensureIds } from './treeUtils'
import { makeStep } from './stepDefaults'
import { moveStep, insertStepAtPath, removeStepAtPath, getStepAtPath, isDescendant } from './treeUtils'
import StepNode, { StepNodeGhost } from './StepNode'

interface StepCanvasProps {
  root: AnyStep
  selectedPath: StepPath
  errors: ValidationError[]
  onChange: (newRoot: AnyStep) => void
  onSelect: (path: StepPath) => void
}

export default function StepCanvas({ root, selectedPath, errors, onChange, onSelect }: StepCanvasProps) {
  const [activeDragData, setActiveDragData] = useState<{ step: AnyStep | null; id: string } | null>(null)

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } })
  )

  function handleDragStart(event: DragStartEvent) {
    const data = event.active.data.current
    if (data?.source === 'palette') {
      setActiveDragData({ step: makeStep(data.stepType as StepType), id: event.active.id as string })
    } else if (data?.source === 'node' && data.path) {
      const step = getStepAtPath(root, data.path)
      setActiveDragData({ step: step ?? null, id: event.active.id as string })
    }
  }

  function handleDragEnd(event: DragEndEvent) {
    setActiveDragData(null)
    const { active, over } = event
    if (!over) return

    const activeData = active.data.current
    const overData = over.data.current

    if (activeData?.source === 'palette') {
      // Create new step at drop target
      const newStep = ensureIds(makeStep(activeData.stepType as StepType))
      const parentPath: StepPath = overData?.parentPath ?? []
      const parent = getStepAtPath(root, parentPath)
      const targetIndex = parent?.steps?.length ?? 0
      const newRoot = insertStepAtPath(root, parentPath, targetIndex, newStep)
      onChange(newRoot)
    } else if (activeData?.source === 'node') {
      const fromPath: StepPath = activeData.path
      // Determine destination
      let toParentPath: StepPath
      let toIndex: number

      if (overData?.parentPath !== undefined) {
        // Dropped on a container body
        toParentPath = overData.parentPath
        const parent = getStepAtPath(root, toParentPath)
        toIndex = parent?.steps?.length ?? 0
      } else if (overData?.path !== undefined) {
        // Dropped on another step node — insert before it
        const overPath: StepPath = overData.path
        toParentPath = overPath.slice(0, -1)
        toIndex = overPath[overPath.length - 1] as number
      } else {
        return
      }

      // Prevent dropping a step into its own descendants
      if (isDescendant(fromPath, [...toParentPath, toIndex])) return

      if (JSON.stringify(fromPath) !== JSON.stringify([...toParentPath, toIndex])) {
        onChange(moveStep(root, fromPath, toParentPath, toIndex))
      }
    }
  }

  function handleDelete(path: StepPath) {
    onChange(removeStepAtPath(root, path))
    if (JSON.stringify(selectedPath).startsWith(JSON.stringify(path))) {
      onSelect([])
    }
  }

  function handleMoveUp(path: StepPath) {
    const idx = path[path.length - 1]
    if (typeof idx !== 'number' || idx <= 0) return
    const parentPath = path.slice(0, -1)
    onChange(moveStep(root, path, parentPath, idx - 1))
    onSelect([...parentPath, idx - 1])
  }

  function handleMoveDown(path: StepPath) {
    const parentPath = path.slice(0, -1)
    const parent = getStepAtPath(root, parentPath)
    const idx = path[path.length - 1]
    if (typeof idx !== 'number' || !parent?.steps || idx >= parent.steps.length - 1) return
    onChange(moveStep(root, path, parentPath, idx + 2))
    onSelect([...parentPath, idx + 1])
  }

  function handleDuplicate(path: StepPath) {
    const idx = path[path.length - 1]
    if (typeof idx !== 'number') return
    const step = getStepAtPath(root, path)
    if (!step) return
    const cloned = ensureIds(JSON.parse(JSON.stringify(step)))
    const parentPath = path.slice(0, -1)
    onChange(insertStepAtPath(root, parentPath, idx + 1, cloned))
  }

  function handleAddChild(parentPath: StepPath, type: StepType) {
    const newStep = ensureIds(makeStep(type))
    const parent = getStepAtPath(root, parentPath)
    const idx = parent?.steps?.length ?? 0
    onChange(insertStepAtPath(root, parentPath, idx, newStep))
    onSelect([...parentPath, idx])
  }

  const rootChildren = root.steps ?? []

  return (
    <DndContext
      sensors={sensors}
      collisionDetection={closestCenter}
      onDragStart={handleDragStart}
      onDragEnd={handleDragEnd}
    >
      <div className="wfe-canvas">
        <SortableContext
          items={rootChildren.map((c, i) => c._id ?? `root-${i}`)}
          strategy={verticalListSortingStrategy}
        >
          {/* Render root node itself if it's not a container, or render its children */}
          <StepNode
            step={root}
            path={[]}
            selectedPath={selectedPath}
            errors={errors}
            depth={0}
            onSelect={onSelect}
            onDelete={handleDelete}
            onMoveUp={handleMoveUp}
            onMoveDown={handleMoveDown}
            onDuplicate={handleDuplicate}
            onAddChild={handleAddChild}
          />
        </SortableContext>
      </div>

      <DragOverlay>
        {activeDragData?.step && <StepNodeGhost step={activeDragData.step} />}
      </DragOverlay>
    </DndContext>
  )
}
