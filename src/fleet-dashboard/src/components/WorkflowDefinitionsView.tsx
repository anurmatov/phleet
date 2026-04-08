import { useState, useRef } from 'react'
import type {
  WorkflowDefinitionSummary,
  WorkflowDefinitionDetail,
  ConfigSaveState,
} from '../types'
import { computeDiff, relativeTime } from '../utils'
import FieldHint from './FieldHint'

const VALID_STEP_TYPES = new Set([
  'sequence', 'parallel', 'loop', 'branch', 'break', 'continue', 'noop',
  'delegate', 'delegate_with_escalation',
  'wait_for_signal', 'child_workflow', 'fire_and_forget', 'cross_namespace_start',
  'set_variable', 'set_attribute', 'http_request',
])

const NS_COLOR_PALETTE = [
  'var(--accent-blue, #4f6ef7)',
  '#a78bfa',
  '#4ade80',
  '#f59e0b',
  '#f87171',
  '#38bdf8',
]

function nsColor(namespace: string, namespaces: string[]): string {
  const idx = namespaces.indexOf(namespace)
  if (idx === -1) return '#94a3b8'
  return NS_COLOR_PALETTE[idx % NS_COLOR_PALETTE.length]
}

interface ValidationError { message: string; blocking: boolean }

function validateDefinition(json: string): ValidationError[] {
  const errors: ValidationError[] = []
  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch (e: unknown) {
    errors.push({ message: `JSON parse error: ${e instanceof Error ? e.message : String(e)}`, blocking: true })
    return errors
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    errors.push({ message: 'Root must be a JSON object', blocking: true })
    return errors
  }
  validateStep(parsed as Record<string, unknown>, errors, 'root')
  return errors
}

function validateStep(step: Record<string, unknown>, errors: ValidationError[], path: string) {
  if (!('type' in step)) {
    errors.push({ message: `${path}: missing "type" field`, blocking: true })
    return
  }
  const type = step.type as string
  if (!VALID_STEP_TYPES.has(type)) {
    errors.push({ message: `${path}: unknown step type "${type}"`, blocking: true })
  }
  if (type === 'delegate' || type === 'delegate_with_escalation') {
    if (!step.target) errors.push({ message: `${path}: "target" is required`, blocking: true })
    if (!step.instruction)
      errors.push({ message: `${path}: "instruction" is required`, blocking: true })
  }
  if (type === 'wait_for_signal') {
    if (!step.signalName) errors.push({ message: `${path}: "signalName" is required`, blocking: true })
  }
  if (type === 'child_workflow' || type === 'fire_and_forget' || type === 'cross_namespace_start') {
    if (!step.workflowType) errors.push({ message: `${path}: "workflowType" is required`, blocking: true })
    if (type === 'cross_namespace_start') {
      if (!step.namespace) errors.push({ message: `${path}: "namespace" is required`, blocking: true })
      if (!step.taskQueue) errors.push({ message: `${path}: "taskQueue" is required`, blocking: true })
    }
  }
  if (type === 'http_request') {
    if (!step.url) errors.push({ message: `${path}: "url" is required`, blocking: true })
  }
  if (type === 'branch') {
    if (!step.on) errors.push({ message: `${path}: "on" expression is required`, blocking: true })
    if (!step.cases || typeof step.cases !== 'object' || Object.keys(step.cases as object).length === 0) {
      errors.push({ message: `${path}: at least one case is required`, blocking: true })
    } else {
      const cases = step.cases as Record<string, unknown>
      Object.entries(cases).forEach(([caseKey, caseVal]) => {
        if (caseVal && typeof caseVal === 'object')
          validateStep(caseVal as Record<string, unknown>, errors, `${path}.cases["${caseKey}"]`)
      })
      if (step.default && typeof step.default === 'object')
        validateStep(step.default as Record<string, unknown>, errors, `${path}.default`)
    }
  }
  if (type === 'loop' || type === 'sequence') {
    const steps = step.steps as unknown[]
    if (!Array.isArray(steps) || steps.length === 0)
      errors.push({ message: `${path}: "steps" must be a non-empty array`, blocking: true })
    else steps.forEach((s, i) => validateStep(s as Record<string, unknown>, errors, `${path}.steps[${i}]`))
  }
  if (type === 'parallel') {
    const steps = step.steps as unknown[]
    if (Array.isArray(steps))
      steps.forEach((s, i) => validateStep(s as Record<string, unknown>, errors, `${path}.steps[${i}]`))
  }
  // Template syntax warnings
  const json = JSON.stringify(step)
  const openCount = (json.match(/\{\{/g) || []).length
  const closeCount = (json.match(/\}\}/g) || []).length
  if (openCount > closeCount) errors.push({ message: `${path}: unclosed {{ expression`, blocking: false })
}

interface WorkflowDefinitionsViewProps {
  definitions: WorkflowDefinitionSummary[]
  loading: boolean
  namespaces: string[]
  expandedDef: string | null
  defDetails: Record<string, WorkflowDefinitionDetail>
  defDetailLoading: Record<string, boolean>
  defEdits: Record<string, string>
  defReasons: Record<string, string>
  defSaveState: Record<string, ConfigSaveState>
  defSaveMsg: Record<string, string>
  defSelectedVersion: Record<string, number | null>
  defRollbackConfirm: Record<string, string | null>
  defToggleConfirm: Record<string, boolean>
  defToggleState: Record<string, 'idle' | 'pending' | 'success' | 'error'>
  defToggleMsg: Record<string, string>
  nsFilter: string
  searchQuery: string
  showNewForm: boolean
  newForm: { name: string; namespace: string; taskQueue: string; description: string; definition: string }
  newFormState: 'idle' | 'saving' | 'success' | 'error'
  newFormMsg: string
  onToggleExpand: (name: string) => void
  onSetEdit: (name: string, value: string) => void
  onSetReason: (name: string, value: string) => void
  onSave: (name: string) => void
  onSelectVersion: (name: string, ver: number | null) => void
  onRollbackClick: (name: string, ver: number) => void
  onToggleActive: (name: string, isActive: boolean) => void
  onToggleConfirmClick: (name: string) => void
  onNsFilter: (ns: string) => void
  onSearch: (q: string) => void
  onShowNewForm: (show: boolean) => void
  onNewFormChange: (field: string, value: string) => void
  onNewFormSubmit: () => void
  onEditVisual?: (name: string) => void
  onNewVisual?: () => void
  onRefresh: () => void
}

export default function WorkflowDefinitionsView({
  definitions, loading, namespaces, expandedDef, defDetails, defDetailLoading,
  defEdits, defReasons, defSaveState, defSaveMsg,
  defSelectedVersion, defRollbackConfirm, defToggleConfirm, defToggleState, defToggleMsg,
  nsFilter, searchQuery, showNewForm, newForm, newFormState, newFormMsg,
  onToggleExpand, onSetEdit, onSetReason, onSave, onSelectVersion, onRollbackClick,
  onToggleActive, onToggleConfirmClick, onNsFilter, onSearch, onShowNewForm, onNewFormChange, onNewFormSubmit,
  onEditVisual, onNewVisual, onRefresh,
}: WorkflowDefinitionsViewProps) {
  const [validatedEdits, setValidatedEdits] = useState<Record<string, string>>({})
  const validationTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const [debouncedNewFormDef, setDebouncedNewFormDef] = useState('')
  const newFormTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const filtered = definitions.filter(d => {
    if (nsFilter !== 'all' && d.namespace !== nsFilter) return false
    if (searchQuery && !d.name.toLowerCase().includes(searchQuery.toLowerCase())) return false
    return true
  })
  const activeCount = definitions.filter(d => d.isActive).length
  const inactiveCount = definitions.length - activeCount

  const defaultTaskQueue = (ns: string) => ns

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          WF Definitions
          {definitions.length > 0 && (
            <span className="section-count">{activeCount} active · {inactiveCount} inactive</span>
          )}
        </h1>
        <div style={{ display: 'flex', gap: 8 }}>
          <button className="completed-refresh-btn" onClick={onRefresh} disabled={loading} title="Refresh definitions">↻</button>
          {onNewVisual && (
            <button className="view-page-action wfe-visual-btn" onClick={onNewVisual}>+ Visual Editor</button>
          )}
          <button className="view-page-action" onClick={() => onShowNewForm(!showNewForm)}>
            {showNewForm ? 'Cancel' : '+ New JSON'}
          </button>
        </div>
      </div>

      {/* Namespace filter + search */}
      <div className="wfd-toolbar">
        <div className="wfd-ns-pills">
          {['all', ...namespaces].map(ns => (
            <button
              key={ns}
              className={`wfd-ns-pill${nsFilter === ns ? ' active' : ''}`}
              onClick={() => onNsFilter(ns)}
            >{ns}</button>
          ))}
        </div>
        <input
          className="config-input wfd-search"
          placeholder="Search definitions…"
          value={searchQuery}
          onChange={e => onSearch(e.target.value)}
        />
      </div>

      {/* New definition inline form */}
      {showNewForm && (
        <div className="wfd-new-form">
          <div className="wfd-new-form-title">New Workflow Definition</div>
          <div className="wfd-new-form-fields">
            <div className="wfd-form-row">
              <label className="config-label">Name <span className="wfd-required">*</span></label>
              <FieldHint>Workflow type name as registered in Temporal (e.g. <code>MyCustomWorkflow</code>). Must be unique per namespace. PascalCase by convention.</FieldHint>
              <input
                className="config-input"
                placeholder="e.g. MyWorkflow"
                value={newForm.name}
                onChange={e => onNewFormChange('name', e.target.value)}
              />
              {newForm.name && !/^[a-zA-Z0-9_-]+$/.test(newForm.name) && (
                <div className="wfd-field-error">No spaces or special characters except - _</div>
              )}
            </div>
            <div className="wfd-form-row wfd-form-row-inline">
              <div>
                <label className="config-label">Namespace <span className="wfd-required">*</span></label>
                <FieldHint>Temporal namespace this workflow runs in (e.g. <code>fleet</code>). Determines which worker picks it up.</FieldHint>
                <select
                  className="config-input"
                  value={newForm.namespace}
                  onChange={e => {
                    onNewFormChange('namespace', e.target.value)
                    onNewFormChange('taskQueue', defaultTaskQueue(e.target.value))
                  }}
                >
                  <option value="">— select —</option>
                  {namespaces.map(ns => <option key={ns} value={ns}>{ns}</option>)}
                </select>
              </div>
              <div>
                <label className="config-label">Task Queue <span className="wfd-required">*</span></label>
                <FieldHint>Temporal task queue name. By convention, matches namespace (e.g. <code>fleet</code> → <code>fleet</code>).</FieldHint>
                <select
                  className="config-input"
                  value={newForm.taskQueue}
                  onChange={e => onNewFormChange('taskQueue', e.target.value)}
                >
                  <option value="">— select —</option>
                  {namespaces.map(ns => <option key={ns} value={ns}>{ns}</option>)}
                </select>
              </div>
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Description</label>
              <FieldHint>Optional human-readable summary shown in the workflow type picker when starting a workflow.</FieldHint>
              <input
                className="config-input"
                placeholder="optional"
                value={newForm.description}
                onChange={e => onNewFormChange('description', e.target.value)}
              />
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Definition JSON <span className="wfd-required">*</span></label>
              <FieldHint>UWE step tree JSON. The root must be a step object with a <code>type</code> field (e.g. <code>sequence</code>, <code>parallel</code>, <code>delegate</code>).</FieldHint>
              <textarea
                className="instr-editor"
                rows={8}
                spellCheck={false}
                placeholder={'{\n  "type": "sequence",\n  "steps": []\n}'}
                value={newForm.definition}
                onChange={e => {
                  onNewFormChange('definition', e.target.value)
                  if (newFormTimerRef.current) clearTimeout(newFormTimerRef.current)
                  newFormTimerRef.current = setTimeout(() => setDebouncedNewFormDef(e.target.value), 500)
                }}
              />
              <NewDefValidation json={debouncedNewFormDef} />
            </div>
          </div>
          <div className="wfd-new-form-actions">
            <button
              className="config-save-btn"
              disabled={
                newFormState === 'saving' ||
                !newForm.name || !/^[a-zA-Z0-9_-]+$/.test(newForm.name) ||
                !newForm.namespace || !newForm.taskQueue ||
                !newForm.definition ||
                validateDefinition(newForm.definition).some(e => e.blocking)
              }
              onClick={onNewFormSubmit}
            >
              {newFormState === 'saving' ? '…' : 'Create'}
            </button>
            <button className="wfd-cancel-btn" onClick={() => onShowNewForm(false)}>Cancel</button>
            {(newFormState === 'success' || newFormState === 'error') && (
              <span className={`config-feedback config-feedback-${newFormState}`}>{newFormMsg}</span>
            )}
          </div>
        </div>
      )}

      {loading && <div className="view-empty">Loading…</div>}
      {!loading && filtered.length === 0 && (
        <div className="view-empty">
          {definitions.length === 0 ? 'No workflow definitions found.' : 'No results match filters.'}
        </div>
      )}

      <div className="instructions-list">
        {filtered.map(def => {
          const isOpen = expandedDef === def.name
          const detail = defDetails[def.name]
          const detailLoading = defDetailLoading[def.name] ?? false
          const editContent = defEdits[def.name] ?? ''
          const reason = defReasons[def.name] ?? ''
          const saveState = defSaveState[def.name] ?? 'idle'
          const saveMsg = defSaveMsg[def.name] ?? ''
          const selVer = defSelectedVersion[def.name] ?? null
          const rbKey = defRollbackConfirm[def.name] ?? null
          const toggleConfirming = defToggleConfirm[def.name] ?? false
          const toggleState = defToggleState[def.name] ?? 'idle'
          const toggleMsg = defToggleMsg[def.name] ?? ''

          const currentDef = detail?.definition ?? ''
          const selVerDef = selVer !== null
            ? (detail?.versions.find(v => v.version === selVer)?.definition ?? '')
            : ''
          const diffLines = selVer !== null && selVerDef ? computeDiff(selVerDef, currentDef) : null
          const addCount = diffLines?.filter(l => l.type === 'add').length ?? 0
          const removeCount = diffLines?.filter(l => l.type === 'remove').length ?? 0

          const validatedContent = validatedEdits[def.name] ?? editContent
          const validationErrors = validatedContent ? validateDefinition(validatedContent) : []
          const hasBlockingErrors = editContent ? validateDefinition(editContent).some(e => e.blocking) : false
          const isDirty = editContent !== currentDef

          return (
            <div
              key={def.name}
              className={`instr-row${!def.isActive ? ' wfd-row-inactive' : ''}`}
            >
              <div className="instr-header" onClick={() => onToggleExpand(def.name)}>
                <span className="instr-name">{def.name}</span>
                <span className="instr-meta">
                  <span
                    className="wfd-ns-badge"
                    style={{ color: nsColor(def.namespace, namespaces) }}
                  >{def.namespace}</span>
                  <span className="instr-version">v{def.version}</span>
                  <span className={`wfd-active-badge${def.isActive ? ' active' : ' inactive'}`}>
                    {def.isActive ? 'active' : 'inactive'}
                  </span>
                  <span className="instr-total">{def.taskQueue}</span>
                  <span className="instr-total">{relativeTime(def.updatedAt)}</span>
                </span>
                <div className="wfd-row-actions" onClick={e => e.stopPropagation()}>
                  {onEditVisual && (
                    <button className="wfe-visual-btn wfe-row-edit-btn" onClick={() => onEditVisual(def.name)}>✎ Edit</button>
                  )}
                  {toggleState === 'idle' || toggleState === 'pending' ? (
                    <button
                      className={`wfd-toggle-btn${toggleConfirming ? ' confirming' : ''}`}
                      disabled={toggleState === 'pending'}
                      onClick={() => {
                        if (toggleConfirming) {
                          onToggleActive(def.name, !def.isActive)
                        } else {
                          onToggleConfirmClick(def.name)
                        }
                      }}
                    >
                      {toggleState === 'pending'
                        ? '…'
                        : toggleConfirming
                          ? `confirm ${def.isActive ? 'disable' : 'enable'}?`
                          : def.isActive ? 'disable' : 'enable'}
                    </button>
                  ) : (
                    <span className={`config-feedback config-feedback-${toggleState}`}>{toggleMsg}</span>
                  )}
                </div>
                <span className="history-toggle">{isOpen ? '▲' : '▼'}</span>
              </div>

              {isOpen && (
                <div className="instr-detail">
                  {detailLoading && <div className="config-loading">Loading…</div>}
                  {!detailLoading && detail && (
                    <div className="instr-body">
                      {/* Left: JSON editor */}
                      <div className="instr-editor-col">
                        <div className="config-label" style={{ marginBottom: 4 }}>
                          Definition JSON (v{detail.version})
                        </div>
                        <textarea
                          className="instr-editor"
                          value={editContent}
                          onChange={e => {
                            onSetEdit(def.name, e.target.value)
                            if (validationTimerRef.current) clearTimeout(validationTimerRef.current)
                            validationTimerRef.current = setTimeout(() => {
                              setValidatedEdits(prev => ({ ...prev, [def.name]: e.target.value }))
                            }, 500)
                          }}
                          rows={20}
                          spellCheck={false}
                        />
                        {/* Validation bar */}
                        {editContent && validationErrors.length > 0 && (
                          <div className="wfd-validation-bar">
                            {validationErrors.map((err, i) => (
                              <div key={i} className={`wfd-validation-item${err.blocking ? ' blocking' : ' warning'}`}>
                                {err.blocking ? '✕' : '⚠'} {err.message}
                              </div>
                            ))}
                          </div>
                        )}
                        <div className="instr-save-row">
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <FieldHint>Short note explaining this version. Shown in version history.</FieldHint>
                            <input
                              className="config-input instr-reason-input"
                              placeholder="reason (optional)"
                              value={reason}
                              onChange={e => onSetReason(def.name, e.target.value)}
                              style={{ width: '100%' }}
                            />
                          </div>
                          <button
                            className="config-save-btn"
                            disabled={saveState === 'saving' || !isDirty || hasBlockingErrors}
                            onClick={() => onSave(def.name)}
                          >
                            {saveState === 'saving' ? '…' : `Save v${detail.version + 1}`}
                          </button>
                          {(saveState === 'success' || saveState === 'error') && (
                            <span className={`config-feedback config-feedback-${saveState}`}>{saveMsg}</span>
                          )}
                        </div>
                      </div>

                      {/* Right: Version history */}
                      <div className="instr-history-col">
                        <div className="config-label" style={{ marginBottom: 4 }}>Version history</div>
                        <div className="instr-version-list">
                          {detail.versions.length === 0 && (
                            <div className="wfd-no-versions">No previous versions.</div>
                          )}
                          {/* Current version entry */}
                          <div className="instr-version-item current">
                            <div className="instr-version-header">
                              <span className="instr-version-num">v{detail.version}</span>
                              <span className="instr-current-badge">current</span>
                            </div>
                            <div className="instr-version-meta">
                              <span>{relativeTime(detail.updatedAt)}</span>
                            </div>
                          </div>
                          {detail.versions.map(v => {
                            const isRbConfirming = rbKey === `${def.name}:${v.version}`
                            const isSelected = selVer === v.version
                            return (
                              <div
                                key={v.version}
                                className={`instr-version-item${isSelected ? ' selected' : ''}`}
                                onClick={() => onSelectVersion(def.name, isSelected ? null : v.version)}
                              >
                                <div className="instr-version-header">
                                  <span className="instr-version-num">v{v.version}</span>
                                  <button
                                    className="wfd-diff-btn"
                                    onClick={e => { e.stopPropagation(); onSelectVersion(def.name, isSelected ? null : v.version) }}
                                  >
                                    {isSelected ? 'hide diff' : 'view diff'}
                                  </button>
                                  <button
                                    className={`instr-rollback-btn${isRbConfirming ? ' confirming' : ''}`}
                                    disabled={saveState === 'saving'}
                                    onClick={e => { e.stopPropagation(); onRollbackClick(def.name, v.version) }}
                                    title={isRbConfirming ? 'Click again to confirm' : `Rollback to v${v.version}`}
                                  >
                                    {isRbConfirming
                                      ? `confirm rollback to v${v.version}?`
                                      : 'rollback'}
                                  </button>
                                </div>
                                <div className="instr-version-meta">
                                  <span>{v.createdAt ? relativeTime(v.createdAt) : ''}</span>
                                  {v.createdBy && <span>{v.createdBy}</span>}
                                </div>
                                {v.reason && <div className="instr-version-reason">{v.reason}</div>}
                              </div>
                            )
                          })}
                        </div>

                        {diffLines && selVer !== null && (
                          <div className="instr-diff">
                            <div className="config-label" style={{ marginBottom: 4 }}>
                              v{selVer} → current · +{addCount} / -{removeCount}
                            </div>
                            <div className="instr-diff-body">
                              {diffLines.map((line, i) => (
                                <div key={i} className={`diff-line diff-${line.type}`}>
                                  <span className="diff-prefix">
                                    {line.type === 'add' ? '+' : line.type === 'remove' ? '-' : ' '}
                                  </span>
                                  <span className="diff-text">
                                    {line.segments
                                      ? line.segments.map((seg, j) => (
                                          <span key={j} className={seg.changed ? 'diff-highlight' : ''}>{seg.text}</span>
                                        ))
                                      : line.text}
                                  </span>
                                </div>
                              ))}
                            </div>
                          </div>
                        )}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}

function NewDefValidation({ json }: { json: string }) {
  if (!json) return null
  const errors = validateDefinition(json)
  if (errors.length === 0) return <div className="wfd-validation-ok">✓ Valid</div>
  return (
    <div className="wfd-validation-bar">
      {errors.map((err, i) => (
        <div key={i} className={`wfd-validation-item${err.blocking ? ' blocking' : ' warning'}`}>
          {err.blocking ? '✕' : '⚠'} {err.message}
        </div>
      ))}
    </div>
  )
}

