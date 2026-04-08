import { useState } from 'react'
import { useSortable } from '@dnd-kit/sortable'
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable'
import { useDroppable } from '@dnd-kit/core'
import { CSS } from '@dnd-kit/utilities'
import type { AnyStep, StepPath, ValidationError } from './editorTypes'
import { STEP_COLORS, CONTAINER_TYPES } from './editorTypes'
import { stepSummary, isBranchCaseTerminal } from './validation'
import { makeStep } from './stepDefaults'
import { genId } from './treeUtils'
import type { StepType } from './editorTypes'

const STEP_TYPES_LIST: StepType[] = [
  'sequence', 'parallel', 'loop', 'branch',
  'break', 'continue', 'noop',
  'delegate', 'delegate_with_escalation',
  'wait_for_signal',
  'child_workflow', 'fire_and_forget', 'cross_namespace_start',
  'set_variable', 'set_attribute', 'http_request',
]

interface StepNodeProps {
  step: AnyStep
  path: StepPath
  selectedPath: StepPath
  errors: ValidationError[]
  depth: number
  onSelect: (path: StepPath) => void
  onDelete: (path: StepPath) => void
  onMoveUp: (path: StepPath) => void
  onMoveDown: (path: StepPath) => void
  onDuplicate: (path: StepPath) => void
  onAddChild: (parentPath: StepPath, type: StepType) => void
}

export default function StepNode({
  step, path, selectedPath, errors, depth,
  onSelect, onDelete, onMoveUp, onMoveDown, onDuplicate, onAddChild,
}: StepNodeProps) {
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number } | null>(null)
  const [addPickerOpen, setAddPickerOpen] = useState(false)

  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: step._id ?? path.join('-'),
    data: { source: 'node', path, stepId: step._id },
  })

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.4 : 1,
  }

  const isSelected = JSON.stringify(path) === JSON.stringify(selectedPath)
  const isContainer = CONTAINER_TYPES.has(step.type)
  const hasErrors = errors.some(e => e.pathStr.startsWith(getPathStr(path)))

  function handleContextMenu(e: React.MouseEvent) {
    e.preventDefault()
    setCtxMenu({ x: e.clientX, y: e.clientY })
  }

  function closeCtx() { setCtxMenu(null) }

  const summary = stepSummary(step)

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`wfe-step-node${isSelected ? ' selected' : ''}${hasErrors ? ' has-errors' : ''}${depth === 0 ? ' depth-0' : ''}`}
    >
      {/* Step header row */}
      <div
        className="wfe-step-header"
        onClick={() => onSelect(path)}
        onContextMenu={handleContextMenu}
        {...listeners}
        {...attributes}
      >
        <span className="wfe-step-drag-handle">⠿</span>
        <span className="wfe-step-type-badge" style={{ borderColor: STEP_COLORS[step.type] ?? '#94a3b8', color: STEP_COLORS[step.type] ?? '#94a3b8' }}>
          {step.type}
        </span>
        {step.name && <span className="wfe-step-name">{step.name}</span>}
        {summary && <span className="wfe-step-summary">{summary}</span>}
        {hasErrors && <span className="wfe-step-error-dot" title="Validation errors">!</span>}
        <button
          className="wfe-step-delete"
          onClick={e => { e.stopPropagation(); onDelete(path) }}
          title="Delete"
        >✕</button>
      </div>

      {/* Children for container steps */}
      {isContainer && step.type !== 'branch' && !step.forEach && (
        <ContainerBody
          step={step}
          path={path}
          selectedPath={selectedPath}
          errors={errors}
          depth={depth}
          onSelect={onSelect}
          onDelete={onDelete}
          onMoveUp={onMoveUp}
          onMoveDown={onMoveDown}
          onDuplicate={onDuplicate}
          onAddChild={onAddChild}
          addPickerOpen={addPickerOpen}
          setAddPickerOpen={setAddPickerOpen}
        />
      )}

      {/* forEach template step display */}
      {step.type === 'parallel' && step.forEach && (
        <div className="wfe-container-body">
          {step.step ? (
            <div className="wfe-step-node" style={{ opacity: 0.85 }}>
              <div className="wfe-step-header">
                <span className="wfe-step-type-badge" style={{ borderColor: STEP_COLORS[step.step.type] ?? '#94a3b8', color: STEP_COLORS[step.step.type] ?? '#94a3b8' }}>
                  {step.step.type}
                </span>
                {step.step.name && <span className="wfe-step-name">{step.step.name}</span>}
                <span className="wfe-step-summary" style={{ fontStyle: 'italic' }}>forEach template</span>
              </div>
            </div>
          ) : (
            <div className="wfe-container-empty">Set template step in panel →</div>
          )}
        </div>
      )}

      {/* Branch cases */}
      {step.type === 'branch' && (step.cases || step.default !== undefined) && (
        <div className="wfe-branch-body">
          {Object.entries(step.cases ?? {}).map(([caseKey, caseVal]) => (
            <div key={caseKey} className="wfe-branch-case-body">
              <div className="wfe-branch-case-label">&quot;{caseKey}&quot;</div>
              {isBranchCaseTerminal(caseVal) ? (
                <div className="wfe-terminal-pill">→ {caseVal.type}</div>
              ) : (
                <StepNode
                  step={caseVal as AnyStep}
                  path={[...path, caseKey]}
                  selectedPath={selectedPath}
                  errors={errors}
                  depth={depth + 1}
                  onSelect={onSelect}
                  onDelete={onDelete}
                  onMoveUp={onMoveUp}
                  onMoveDown={onMoveDown}
                  onDuplicate={onDuplicate}
                  onAddChild={onAddChild}
                />
              )}
            </div>
          ))}
          {step.default !== undefined && (
            <div className="wfe-branch-case-body">
              <div className="wfe-branch-case-label">default</div>
              {isBranchCaseTerminal(step.default) ? (
                <div className="wfe-terminal-pill">→ {step.default.type}</div>
              ) : (
                <div className="wfe-step-node" style={{ opacity: 0.85 }}>
                  <div className="wfe-step-header">
                    <span className="wfe-step-type-badge" style={{ borderColor: STEP_COLORS[(step.default as AnyStep).type] ?? '#94a3b8', color: STEP_COLORS[(step.default as AnyStep).type] ?? '#94a3b8' }}>
                      {(step.default as AnyStep).type}
                    </span>
                    {(step.default as AnyStep).name && <span className="wfe-step-name">{(step.default as AnyStep).name}</span>}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {/* Context menu */}
      {ctxMenu && (() => {
        const isKeyed = typeof path[path.length - 1] === 'string'
        return (
          <div
            className="wfe-ctx-menu"
            style={{ position: 'fixed', top: ctxMenu.y, left: ctxMenu.x }}
            onMouseLeave={closeCtx}
          >
            {!isKeyed && <><div className="wfe-ctx-item" onClick={() => { onMoveUp(path); closeCtx() }}>Move Up</div>
            <div className="wfe-ctx-item" onClick={() => { onMoveDown(path); closeCtx() }}>Move Down</div>
            <div className="wfe-ctx-item" onClick={() => { onDuplicate(path); closeCtx() }}>Duplicate</div>
            <div className="wfe-ctx-divider" /></>}
            <div className="wfe-ctx-item wfe-ctx-delete" onClick={() => { onDelete(path); closeCtx() }}>Delete</div>
          </div>
        )
      })()}
    </div>
  )
}

// ─── Container body ───────────────────────────────────────────────────────────

interface ContainerBodyProps {
  step: AnyStep
  path: StepPath
  selectedPath: StepPath
  errors: ValidationError[]
  depth: number
  onSelect: (p: StepPath) => void
  onDelete: (p: StepPath) => void
  onMoveUp: (p: StepPath) => void
  onMoveDown: (p: StepPath) => void
  onDuplicate: (p: StepPath) => void
  onAddChild: (parentPath: StepPath, type: StepType) => void
  addPickerOpen: boolean
  setAddPickerOpen: (v: boolean) => void
}

function ContainerBody({ step, path, selectedPath, errors, depth, onSelect, onDelete, onMoveUp, onMoveDown, onDuplicate, onAddChild, addPickerOpen, setAddPickerOpen }: ContainerBodyProps) {
  const children = step.steps ?? []
  const droppableId = `container:${step._id ?? path.join('-')}`
  const { setNodeRef: setDropRef, isOver } = useDroppable({ id: droppableId, data: { parentPath: path } })

  const isParallel = step.type === 'parallel' && !step.forEach

  return (
    <div
      ref={setDropRef}
      className={`wfe-container-body${isOver && children.length === 0 ? ' drop-over' : ''}${isParallel ? ' parallel-layout' : ''}`}
    >
      <SortableContext
        items={children.map((c, i) => c._id ?? `${path.join('-')}-${i}`)}
        strategy={verticalListSortingStrategy}
      >
        {children.map((child, i) => (
          <StepNode
            key={child._id ?? i}
            step={child}
            path={[...path, i]}
            selectedPath={selectedPath}
            errors={errors}
            depth={depth + 1}
            onSelect={onSelect}
            onDelete={onDelete}
            onMoveUp={onMoveUp}
            onMoveDown={onMoveDown}
            onDuplicate={onDuplicate}
            onAddChild={onAddChild}
          />
        ))}
      </SortableContext>
      {children.length === 0 && !isOver && (
        <div className="wfe-container-empty">Drop steps here</div>
      )}

      {/* + Add step picker */}
      {addPickerOpen ? (
        <div className="wfe-add-picker">
          {STEP_TYPES_LIST.map(t => (
            <button
              key={t}
              className="wfe-add-picker-item"
              style={{ color: STEP_COLORS[t] ?? '#94a3b8' }}
              onClick={() => { onAddChild(path, t); setAddPickerOpen(false) }}
            >{t}</button>
          ))}
          <button className="wfe-add-picker-cancel" onClick={() => setAddPickerOpen(false)}>Cancel</button>
        </div>
      ) : (
        <button className="wfe-add-child-btn" onClick={() => setAddPickerOpen(true)}>+ Add step</button>
      )}
    </div>
  )
}

function getPathStr(path: StepPath): string {
  return path.length === 0 ? 'root' : `root.steps[${path.join('].steps[')}]`
}

// Drag overlay ghost
export function StepNodeGhost({ step }: { step: AnyStep }) {
  return (
    <div className="wfe-step-node wfe-step-ghost">
      <div className="wfe-step-header">
        <span className="wfe-step-type-badge" style={{ borderColor: STEP_COLORS[step.type] ?? '#94a3b8', color: STEP_COLORS[step.type] ?? '#94a3b8' }}>
          {step.type}
        </span>
        {step.name && <span className="wfe-step-name">{step.name}</span>}
      </div>
    </div>
  )
}
