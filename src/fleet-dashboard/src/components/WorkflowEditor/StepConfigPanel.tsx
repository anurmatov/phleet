import { useState, useRef, useEffect } from 'react'
import type { AnyStep, StepPath, StepType } from './editorTypes'
import { STEP_COLORS, STEP_TYPES } from './editorTypes'
import { updateStepAtPath, updateBranchCase, removeBranchCase, renameBranchCase, genId, ensureIds, getStepAtPath } from './treeUtils'
import { makeStep } from './stepDefaults'
import KeyValueEditor from './KeyValueEditor'
import { apiFetch } from '../../utils'
import { parseSchema, SchemaField, WorkflowTypeSelector, convertFieldValue } from './schemaUtils'
import type { SchemaProperty } from './schemaUtils'
import type { WorkflowTypeInfo } from '../../types'

// Convert a stored (typed) arg value back to the string|boolean SchemaField expects
function toFormValue(value: unknown, prop: SchemaProperty): string | boolean {
  if (value === undefined || value === null) return prop.type === 'boolean' ? false : ''
  if (prop.type === 'boolean') return typeof value === 'boolean' ? value : value === 'true'
  if (prop.type === 'array' && Array.isArray(value)) return value.join(', ')
  return String(value)
}

// Fallback agent list — overridden at runtime by GET /api/agents
const DEFAULT_AGENT_CHIPS: string[] = []
// Fallback attribute names — overridden at runtime by GET /api/search-attributes
const DEFAULT_ATTRIBUTE_NAMES = ['IssueNumber', 'PrNumber', 'Phase', 'Repo', 'DocPrs', 'ReviewDate', 'PositionId']

interface StepConfigPanelProps {
  root: AnyStep
  selectedPath: StepPath
  onChange: (newRoot: AnyStep) => void
  workflowTypes?: WorkflowTypeInfo[]
}

export default function StepConfigPanel({ root, selectedPath, onChange, workflowTypes = [] }: StepConfigPanelProps) {
  const [agentNames, setAgentNames] = useState<string[]>(DEFAULT_AGENT_CHIPS)
  const [attributeNames, setAttributeNames] = useState<string[]>(DEFAULT_ATTRIBUTE_NAMES)

  useEffect(() => {
    apiFetch('/api/agents')
      .then(r => r.json())
      .then((data: Array<{ name: string }>) => {
        const names = data.map(a => a.name).filter(Boolean)
        if (names.length > 0) setAgentNames(names)
      })
      .catch(() => {})
    apiFetch('/api/search-attributes')
      .then(r => r.json())
      .then((data: string[]) => {
        if (Array.isArray(data) && data.length > 0) setAttributeNames(data)
      })
      .catch(() => {})
  }, [])

  const step = getStepAtPath(root, selectedPath)
  if (!step) {
    return <div className="wfe-config-empty">Select a step to configure</div>
  }

  function updateStep(updater: (s: AnyStep) => AnyStep) {
    onChange(updateStepAtPath(root, selectedPath, updater))
  }

  function setField<K extends keyof AnyStep>(key: K, value: AnyStep[K]) {
    updateStep(s => ({ ...s, [key]: value }))
  }

  // Minimal AgentState objects for SchemaField agent pills (names only)
  const minimalAgents: import('../../types').AgentState[] = agentNames.map(name => ({
    agentName: name,
    displayName: null,
    shortName: null,
    model: null,
    role: null,
    currentTask: null,
    currentTaskId: null,
    reportedStatus: 'unknown',
    effectiveStatus: 'running',
    lastSeen: '',
    version: null,
    queuedCount: 0,
    queuedMessages: null,
    backgroundTasks: null,
    containerName: null,
    containerStartedAt: null,
    hostPort: null,
  }))

  return (
    <div className="wfe-config-panel">
      <div className="wfe-config-title">
        <span className="wfe-config-type" style={{ color: STEP_COLORS[step.type] ?? '#94a3b8' }}>
          {step.type}
        </span>
      </div>

      {/* Common fields */}
      <CommonFields step={step} setField={setField} />

      {/* Type-specific fields */}
      {(step.type === 'delegate' || step.type === 'delegate_with_escalation') && (
        <DelegateFields step={step} setField={setField} agentNames={agentNames} />
      )}
      {step.type === 'wait_for_signal' && (
        <WaitForSignalFields step={step} setField={setField} agentNames={agentNames} />
      )}
      {step.type === 'branch' && (
        <BranchFields step={step} path={selectedPath} root={root} onChange={onChange} />
      )}
      {step.type === 'loop' && (
        <LoopFields step={step} setField={setField} />
      )}
      {step.type === 'parallel' && (
        <ParallelFields step={step} setField={setField} agentNames={agentNames} />
      )}
      {(step.type === 'child_workflow' || step.type === 'fire_and_forget') && (
        <ChildWorkflowFields step={step} setField={setField} workflowTypes={workflowTypes} agents={minimalAgents} />
      )}
      {step.type === 'cross_namespace_start' && (
        <CrossNamespaceFields step={step} setField={setField} workflowTypes={workflowTypes} agents={minimalAgents} />
      )}
      {step.type === 'set_attribute' && (
        <SetAttributeFields step={step} setField={setField} attributeNames={attributeNames} />
      )}
      {step.type === 'http_request' && (
        <HttpRequestFields step={step} setField={setField} />
      )}
    </div>
  )
}

// ─── Common fields ───────────────────────────────────────────────────────────

interface FieldProps<T extends AnyStep = AnyStep> {
  step: T
  setField: <K extends keyof AnyStep>(k: K, v: AnyStep[K]) => void
}

function CommonFields({ step, setField }: FieldProps) {
  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">name</label>
        <input className="config-input" value={step.name ?? ''} onChange={e => setField('name', e.target.value || undefined)} placeholder="optional" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">outputVar</label>
        <input className="config-input" value={step.outputVar ?? ''} onChange={e => setField('outputVar', e.target.value || undefined)} placeholder="optional" />
      </div>
      <div className="wfe-config-row wfe-config-row-toggle">
        <label className="wfe-config-label">ignoreFailure</label>
        <input type="checkbox" checked={step.ignoreFailure ?? false} onChange={e => setField('ignoreFailure', e.target.checked || undefined)} />
      </div>
    </div>
  )
}

// ─── Delegate fields ──────────────────────────────────────────────────────────

interface DelegateFieldsProps extends FieldProps {
  agentNames: string[]
}

function DelegateFields({ step, setField, agentNames }: DelegateFieldsProps) {
  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">target *</label>
        <div>
          <input className="config-input" value={step.target ?? ''} onChange={e => setField('target', e.target.value)} placeholder="agent name" />
          <div className="wfe-agent-chips">
            {agentNames.map(a => (
              <button key={a} className={`wfe-chip${step.target === a ? ' active' : ''}`} onClick={() => setField('target', a)}>{a}</button>
            ))}
          </div>
        </div>
      </div>

      <div className="wfe-config-row">
        <label className="wfe-config-label">instruction *</label>
        <ExprInput value={step.instruction ?? ''} onChange={v => setField('instruction', v)} rows={4} placeholder="task description..." />
      </div>

      <div className="wfe-config-row">
        <label className="wfe-config-label">timeoutMinutes</label>
        <input className="config-input" type="number" value={step.timeoutMinutes ?? 30} onChange={e => setField('timeoutMinutes', Number(e.target.value))} />
      </div>
      <div className="wfe-config-row wfe-config-row-toggle">
        <label className="wfe-config-label">retryOnIncomplete</label>
        <input type="checkbox" checked={step.retryOnIncomplete ?? true} onChange={e => setField('retryOnIncomplete', e.target.checked)} />
      </div>
      {(step.retryOnIncomplete ?? true) && (
        <div className="wfe-config-row">
          <label className="wfe-config-label">maxIncompleteRetries</label>
          <input className="config-input" type="number" value={step.maxIncompleteRetries ?? 3} onChange={e => setField('maxIncompleteRetries', Number(e.target.value))} />
        </div>
      )}
    </div>
  )
}

// ─── WaitForSignal fields ─────────────────────────────────────────────────────

interface WaitForSignalFieldsProps extends FieldProps {
  agentNames: string[]
}

function WaitForSignalFields({ step, setField, agentNames }: WaitForSignalFieldsProps) {
  const hasNotify = !!step.notifyStep

  function addNotifyStep() {
    setField('notifyStep', ensureIds(makeStep('delegate')))
  }

  function removeNotifyStep() {
    setField('notifyStep', undefined)
  }

  function setNotifyField<K extends keyof AnyStep>(key: K, value: AnyStep[K]) {
    setField('notifyStep', { ...(step.notifyStep ?? makeStep('delegate')), [key]: value } as AnyStep)
  }

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">signalName *</label>
        <input className="config-input" value={step.signalName ?? ''} onChange={e => setField('signalName', e.target.value)} />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">phase</label>
        <input className="config-input" value={step.phase ?? ''} onChange={e => setField('phase', e.target.value || undefined)} />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">timeoutMinutes</label>
        <input className="config-input" type="number" value={step.timeoutMinutes ?? ''} onChange={e => setField('timeoutMinutes', e.target.value ? Number(e.target.value) : undefined)} placeholder="indefinite" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">reminderInterval</label>
        <input className="config-input" type="number" value={step.reminderIntervalMinutes ?? ''} onChange={e => setField('reminderIntervalMinutes', e.target.value ? Number(e.target.value) : undefined)} placeholder="optional" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">maxReminders</label>
        <input className="config-input" type="number" value={step.maxReminders ?? 3} onChange={e => setField('maxReminders', Number(e.target.value))} />
      </div>
      <div className="wfe-config-row wfe-config-row-toggle">
        <label className="wfe-config-label">autoCompleteOnTimeout</label>
        <input type="checkbox" checked={step.autoCompleteOnTimeout ?? false} onChange={e => setField('autoCompleteOnTimeout', e.target.checked || undefined)} />
      </div>
      <div className="wfe-config-row wfe-config-row-toggle">
        <label className="wfe-config-label">notifyStep</label>
        {hasNotify
          ? <button className="wfe-btn-sm" onClick={removeNotifyStep}>Remove</button>
          : <button className="wfe-btn-sm" onClick={addNotifyStep}>Add</button>
        }
      </div>
      {hasNotify && step.notifyStep && (
        <div className="wfe-notify-step-section">
          <div className="wfe-config-sublabel">notifyStep config</div>
          <DelegateFields step={step.notifyStep} setField={setNotifyField} agentNames={agentNames} />
        </div>
      )}
    </div>
  )
}

// ─── Branch fields ────────────────────────────────────────────────────────────

interface BranchFieldsProps {
  step: AnyStep
  path: StepPath
  root: AnyStep
  onChange: (newRoot: AnyStep) => void
}

function BranchFields({ step, path, root, onChange }: BranchFieldsProps) {
  const cases = step.cases ?? {}
  const [editingKey, setEditingKey] = useState<Record<string, string>>({})

  function setOn(val: string) {
    onChange(updateStepAtPath(root, path, s => ({ ...s, on: val })))
  }

  function addCase() {
    const key = `case${Object.keys(cases).length + 1}`
    onChange(updateBranchCase(root, path, key, makeStep('break')))
  }

  function removeCase(key: string) {
    onChange(removeBranchCase(root, path, key))
  }

  function setCaseType(key: string, type: 'action' | 'step') {
    const val = type === 'action' ? makeStep('break') : ensureIds(makeStep('delegate'))
    onChange(updateBranchCase(root, path, key, val))
  }

  function setCaseAction(key: string, action: 'break' | 'continue') {
    onChange(updateBranchCase(root, path, key, makeStep(action)))
  }

  function setCaseStepType(key: string, stepType: string) {
    const newStep = ensureIds(makeStep(stepType as import('./editorTypes').StepType))
    onChange(updateBranchCase(root, path, key, newStep))
  }

  function renameKey(oldKey: string, newKey: string) {
    if (newKey && newKey !== oldKey && !cases[newKey]) {
      onChange(renameBranchCase(root, path, oldKey, newKey))
      setEditingKey(prev => { const n = { ...prev }; delete n[oldKey]; return n })
    }
  }

  // Default case helpers
  const defaultCase = step.default

  function setDefaultType(type: 'action' | 'step') {
    const val = type === 'action' ? makeStep('break') : ensureIds(makeStep('delegate'))
    onChange(updateStepAtPath(root, path, s => ({ ...s, default: val })))
  }

  function setDefaultAction(action: 'break' | 'continue') {
    onChange(updateStepAtPath(root, path, s => ({ ...s, default: makeStep(action) })))
  }

  function setDefaultStepType(stepType: string) {
    const newStep = ensureIds(makeStep(stepType as StepType))
    onChange(updateStepAtPath(root, path, s => ({ ...s, default: newStep })))
  }

  function clearDefault() {
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    onChange(updateStepAtPath(root, path, ({ default: _d, ...rest }) => rest as AnyStep))
  }

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">on *</label>
        <ExprInput value={step.on ?? ''} onChange={setOn} placeholder="{{vars.result}}" />
      </div>
      <div className="wfe-config-label" style={{ marginBottom: 6 }}>Cases</div>
      <div className="wfe-branch-cases">
        {Object.entries(cases).map(([key, val]) => {
          const isAction = (val as AnyStep).type === 'break' || (val as AnyStep).type === 'continue'
          return (
            <div key={key} className="wfe-branch-case">
              <div className="wfe-branch-case-header">
                <input
                  className="config-input wfe-branch-key"
                  value={editingKey[key] ?? key}
                  onChange={e => setEditingKey(prev => ({ ...prev, [key]: e.target.value }))}
                  onBlur={e => renameKey(key, e.target.value)}
                  placeholder="case value"
                />
                <select
                  className="config-input wfe-branch-type-sel"
                  value={isAction ? 'action' : 'step'}
                  onChange={e => setCaseType(key, e.target.value as 'action' | 'step')}
                >
                  <option value="action">action</option>
                  <option value="step">step</option>
                </select>
                {isAction ? (
                  <select
                    className="config-input wfe-branch-action-sel"
                    value={(val as AnyStep).type}
                    onChange={e => setCaseAction(key, e.target.value as 'break' | 'continue')}
                  >
                    <option value="break">break</option>
                    <option value="continue">continue</option>
                  </select>
                ) : (
                  <select
                    className="config-input wfe-branch-step-sel"
                    value={(val as AnyStep).type}
                    onChange={e => setCaseStepType(key, e.target.value)}
                  >
                    {STEP_TYPES.map(t => <option key={t} value={t}>{t}</option>)}
                  </select>
                )}
                <button className="wfe-kv-remove" onClick={() => removeCase(key)}>✕</button>
              </div>
            </div>
          )
        })}
        <button className="wfe-add-btn" onClick={addCase}>+ Add case</button>
      </div>

      <div className="wfe-config-label" style={{ marginBottom: 6, marginTop: 12 }}>Default case</div>
      <div className="wfe-branch-cases">
        {defaultCase !== undefined ? (
          <div className="wfe-branch-case">
            <div className="wfe-branch-case-header">
              <span className="wfe-branch-key-label">default</span>
              <select
                className="config-input wfe-branch-type-sel"
                value={(defaultCase as AnyStep).type === 'break' || (defaultCase as AnyStep).type === 'continue' ? 'action' : 'step'}
                onChange={e => setDefaultType(e.target.value as 'action' | 'step')}
              >
                <option value="action">action</option>
                <option value="step">step</option>
              </select>
              {((defaultCase as AnyStep).type === 'break' || (defaultCase as AnyStep).type === 'continue') ? (
                <select
                  className="config-input wfe-branch-action-sel"
                  value={(defaultCase as AnyStep).type}
                  onChange={e => setDefaultAction(e.target.value as 'break' | 'continue')}
                >
                  <option value="break">break</option>
                  <option value="continue">continue</option>
                </select>
              ) : (
                <select
                  className="config-input wfe-branch-step-sel"
                  value={(defaultCase as AnyStep).type}
                  onChange={e => setDefaultStepType(e.target.value)}
                >
                  {STEP_TYPES.map(t => <option key={t} value={t}>{t}</option>)}
                </select>
              )}
              <button className="wfe-kv-remove" onClick={clearDefault}>✕</button>
            </div>
          </div>
        ) : (
          <button className="wfe-add-btn" onClick={() => setDefaultType('action')}>+ Add default case</button>
        )}
      </div>
    </div>
  )
}

// ─── Loop fields ──────────────────────────────────────────────────────────────

function LoopFields({ step, setField }: FieldProps) {
  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">maxIterations</label>
        <input className="config-input" type="number" value={step.maxIterations ?? 5} onChange={e => setField('maxIterations', Number(e.target.value))} />
      </div>
    </div>
  )
}

// ─── Parallel fields ──────────────────────────────────────────────────────────

interface ParallelFieldsProps extends FieldProps {
  agentNames: string[]
}

function ParallelFields({ step, setField, agentNames }: ParallelFieldsProps) {
  const isForEach = !!(step.forEach !== undefined && step.forEach !== '') || !!(step.itemVar)

  function setTemplateStepType(type: string) {
    setField('step', ensureIds(makeStep(type as StepType)))
  }

  function setTemplateField<K extends keyof AnyStep>(key: K, value: AnyStep[K]) {
    setField('step', { ...(step.step ?? makeStep('delegate')), [key]: value } as AnyStep)
  }

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row wfe-config-row-toggle">
        <label className="wfe-config-label">forEach mode</label>
        <input type="checkbox" checked={isForEach} onChange={e => {
          if (e.target.checked) setField('forEach', '')
          else { setField('forEach', undefined); setField('itemVar', undefined); setField('step', undefined) }
        }} />
      </div>
      {isForEach && (
        <>
          <div className="wfe-config-row">
            <label className="wfe-config-label">forEach *</label>
            <ExprInput value={step.forEach ?? ''} onChange={v => setField('forEach', v)} placeholder="{{vars.items}}" />
          </div>
          <div className="wfe-config-row">
            <label className="wfe-config-label">itemVar *</label>
            <input className="config-input" value={step.itemVar ?? ''} onChange={e => setField('itemVar', e.target.value)} placeholder="item" />
          </div>
          <div className="wfe-config-row">
            <label className="wfe-config-label">step type *</label>
            <select className="config-input" value={step.step?.type ?? ''} onChange={e => setTemplateStepType(e.target.value)}>
              <option value="">— select —</option>
              {STEP_TYPES.map(t => <option key={t} value={t}>{t}</option>)}
            </select>
          </div>
          {step.step && (step.step.type === 'delegate' || step.step.type === 'delegate_with_escalation') && (
            <div className="wfe-notify-step-section">
              <div className="wfe-config-sublabel">template step config</div>
              <DelegateFields step={step.step} setField={setTemplateField} agentNames={agentNames} />
            </div>
          )}
          {step.step && step.step.type !== 'delegate' && step.step.type !== 'delegate_with_escalation' && (
            <div className="wfe-config-row">
              <span className="wfe-config-note">Step type &quot;{step.step.type}&quot; configured ✓</span>
            </div>
          )}
        </>
      )}
    </div>
  )
}

// ─── ChildWorkflow fields ─────────────────────────────────────────────────────

interface ChildWorkflowFieldsProps extends FieldProps {
  workflowTypes: WorkflowTypeInfo[]
  agents: import('../../types').AgentState[]
}

function ChildWorkflowFields({ step, setField, workflowTypes, agents }: ChildWorkflowFieldsProps) {
  const selectedType = workflowTypes.find(t => t.name === step.workflowType)
  const schema = selectedType ? parseSchema(selectedType.inputSchema) : null
  const schemaKeys = new Set(Object.keys(schema?.properties ?? {}))
  const extraRows = Object.entries(step.args ?? {})
    .filter(([key]) => !schemaKeys.has(key))
    .map(([key, value]) => ({ key, value: String(value) }))
  const argsRows = Object.entries(step.args ?? {}).map(([key, value]) => ({ key, value: String(value) }))

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">workflowType *</label>
        <WorkflowTypeSelector
          value={step.workflowType ?? ''}
          workflowTypes={workflowTypes}
          onSelect={(name, ns, tq) => {
            setField('workflowType', name)
            if (ns !== undefined) setField('namespace', ns)
            if (tq !== undefined) setField('taskQueue', tq)
            setField('args', {})
          }}
        />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">namespace</label>
        <input className="config-input" value={step.namespace ?? ''} onChange={e => setField('namespace', e.target.value || undefined)} placeholder="optional" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">taskQueue</label>
        <input className="config-input" value={step.taskQueue ?? ''} onChange={e => setField('taskQueue', e.target.value || undefined)} />
      </div>
      <div className="wfe-config-row wfe-config-row-col">
        <label className="wfe-config-label">args</label>
        {schema?.hasFields ? (
          <div className="wfe-schema-args">
            {Object.entries(schema.properties).map(([key, prop]) => (
              <SchemaField
                key={key}
                fieldKey={key}
                prop={prop}
                value={toFormValue(step.args?.[key], prop)}
                required={schema.required.includes(key)}
                onChange={v => {
                  const typed = convertFieldValue(v, prop, key)
                  if (typed === undefined) {
                    const { [key]: _, ...rest } = (step.args ?? {})
                    setField('args', rest)
                  } else {
                    setField('args', { ...(step.args ?? {}), [key]: typed })
                  }
                }}
                agents={agents}
              />
            ))}
            <details className="wfe-custom-args">
              <summary>Custom args</summary>
              <KeyValueEditor
                rows={extraRows}
                valuePlaceholder="JSON value or {{expr}}"
                onChange={rows => {
                  const schemaArgs: Record<string, unknown> = {}
                  for (const k of schemaKeys) {
                    if (step.args?.[k] !== undefined) schemaArgs[k] = step.args[k]
                  }
                  setField('args', { ...schemaArgs, ...Object.fromEntries(rows.map(r => [r.key, r.value])) })
                }}
              />
            </details>
          </div>
        ) : (
          <KeyValueEditor
            rows={argsRows}
            valuePlaceholder="JSON value"
            onChange={rows => setField('args', Object.fromEntries(rows.map(r => [r.key, r.value])))}
          />
        )}
      </div>
    </div>
  )
}

// ─── CrossNamespace fields ────────────────────────────────────────────────────

interface CrossNamespaceFieldsProps extends FieldProps {
  workflowTypes: WorkflowTypeInfo[]
  agents: import('../../types').AgentState[]
}

function CrossNamespaceFields({ step, setField, workflowTypes, agents }: CrossNamespaceFieldsProps) {
  const selectedType = workflowTypes.find(t => t.name === step.workflowType)
  const schema = selectedType ? parseSchema(selectedType.inputSchema) : null
  const schemaKeys = new Set(Object.keys(schema?.properties ?? {}))
  const extraRows = Object.entries(step.args ?? {})
    .filter(([key]) => !schemaKeys.has(key))
    .map(([key, value]) => ({ key, value: String(value) }))
  const argsRows = Object.entries(step.args ?? {}).map(([key, value]) => ({ key, value: String(value) }))

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">workflowType *</label>
        <WorkflowTypeSelector
          value={step.workflowType ?? ''}
          workflowTypes={workflowTypes}
          onSelect={(name, ns, tq) => {
            setField('workflowType', name)
            if (ns !== undefined) setField('namespace', ns)
            if (tq !== undefined) setField('taskQueue', tq)
            setField('args', {})
          }}
        />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">namespace *</label>
        <input className="config-input" value={step.namespace ?? ''} onChange={e => setField('namespace', e.target.value)} />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">taskQueue *</label>
        <input className="config-input" value={step.taskQueue ?? ''} onChange={e => setField('taskQueue', e.target.value)} />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">workflowId</label>
        <input className="config-input" value={step.workflowId ?? ''} onChange={e => setField('workflowId', e.target.value || undefined)} />
      </div>
      <div className="wfe-config-row wfe-config-row-col">
        <label className="wfe-config-label">args</label>
        {schema?.hasFields ? (
          <div className="wfe-schema-args">
            {Object.entries(schema.properties).map(([key, prop]) => (
              <SchemaField
                key={key}
                fieldKey={key}
                prop={prop}
                value={toFormValue(step.args?.[key], prop)}
                required={schema.required.includes(key)}
                onChange={v => {
                  const typed = convertFieldValue(v, prop, key)
                  if (typed === undefined) {
                    const { [key]: _, ...rest } = (step.args ?? {})
                    setField('args', rest)
                  } else {
                    setField('args', { ...(step.args ?? {}), [key]: typed })
                  }
                }}
                agents={agents}
              />
            ))}
            <details className="wfe-custom-args">
              <summary>Custom args</summary>
              <KeyValueEditor
                rows={extraRows}
                valuePlaceholder="JSON value or {{expr}}"
                onChange={rows => {
                  const schemaArgs: Record<string, unknown> = {}
                  for (const k of schemaKeys) {
                    if (step.args?.[k] !== undefined) schemaArgs[k] = step.args[k]
                  }
                  setField('args', { ...schemaArgs, ...Object.fromEntries(rows.map(r => [r.key, r.value])) })
                }}
              />
            </details>
          </div>
        ) : (
          <KeyValueEditor
            rows={argsRows}
            valuePlaceholder="JSON value"
            onChange={rows => setField('args', Object.fromEntries(rows.map(r => [r.key, r.value])))}
          />
        )}
      </div>
    </div>
  )
}

// ─── SetAttribute fields ──────────────────────────────────────────────────────

function SetAttributeFields({ step, setField, attributeNames }: FieldProps & { attributeNames: string[] }) {
  const attrs = step.attributes ?? []
  function updateAttr(i: number, field: 'name' | 'value', val: string) {
    const next = attrs.map((a, idx) => idx === i ? { ...a, [field]: val } : a)
    setField('attributes', next)
  }
  function removeAttr(i: number) {
    setField('attributes', attrs.filter((_, idx) => idx !== i))
  }
  function addAttr() {
    setField('attributes', [...attrs, { name: '', value: '' }])
  }
  return (
    <div className="wfe-config-section">
      <div className="wfe-config-label" style={{ marginBottom: 6 }}>Attributes</div>
      {attrs.map((attr, i) => (
        <div key={i} className="wfe-kv-row">
          <select className="config-input wfe-kv-key" value={attr.name} onChange={e => updateAttr(i, 'name', e.target.value)}>
            <option value="">— select —</option>
            {attributeNames.map((n: string) => <option key={n} value={n}>{n}</option>)}
          </select>
          <input className="config-input wfe-kv-val" value={attr.value} onChange={e => updateAttr(i, 'value', e.target.value)} placeholder="value or {{expr}}" />
          <button className="wfe-kv-remove" onClick={() => removeAttr(i)}>✕</button>
        </div>
      ))}
      <button className="wfe-add-btn" onClick={addAttr}>+ Add attribute</button>
    </div>
  )
}

// ─── HttpRequest fields ───────────────────────────────────────────────────────

function HttpRequestFields({ step, setField }: FieldProps) {
  const headersRows = (step.headers ?? []).map(h => ({ key: h.name, value: h.value }))
  const statusCodesStr = (step.expectedStatusCodes ?? []).join(', ')

  function setHeaders(rows: Array<{ key: string; value: string }>) {
    setField('headers', rows.map(r => ({ name: r.key, value: r.value })))
  }

  function setExpectedStatusCodes(val: string) {
    const codes = val.split(',').map(s => parseInt(s.trim(), 10)).filter(n => !isNaN(n))
    setField('expectedStatusCodes', codes.length > 0 ? codes : undefined)
  }

  return (
    <div className="wfe-config-section">
      <div className="wfe-config-row">
        <label className="wfe-config-label">url *</label>
        <ExprInput value={step.url ?? ''} onChange={v => setField('url', v)} placeholder="https://api.example.com/{{vars.id}}" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">method</label>
        <select className="config-input" value={step.method ?? 'GET'} onChange={e => setField('method', e.target.value)}>
          <option value="GET">GET</option>
          <option value="POST">POST</option>
          <option value="PUT">PUT</option>
          <option value="PATCH">PATCH</option>
          <option value="DELETE">DELETE</option>
        </select>
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">timeoutSeconds</label>
        <input className="config-input" type="number" value={step.timeoutSeconds ?? 30} onChange={e => setField('timeoutSeconds', Number(e.target.value))} />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">expectedStatusCodes</label>
        <input className="config-input" value={statusCodesStr} onChange={e => setExpectedStatusCodes(e.target.value)} placeholder="200, 201" />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">headers</label>
        <KeyValueEditor
          rows={headersRows}
          valuePlaceholder="value or {{expr}}"
          onChange={setHeaders}
        />
      </div>
      <div className="wfe-config-row">
        <label className="wfe-config-label">body</label>
        <ExprInput value={step.body ?? ''} onChange={v => setField('body', v || undefined)} rows={3} placeholder="JSON body or {{vars.payload}}" />
      </div>
    </div>
  )
}

// ─── Expression input with {{ autocomplete ────────────────────────────────────

interface ExprInputProps {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  rows?: number
}

function ExprInput({ value, onChange, placeholder, rows }: ExprInputProps) {
  const [showAc, setShowAc] = useState(false)
  const ref = useRef<HTMLTextAreaElement>(null)

  const SCOPES = ['input.', 'vars.', 'config.']
  const FILTERS = ['| default:', '| extract:', '| json']

  function handleKeyDown(e: React.KeyboardEvent) {
    const val = (e.target as HTMLTextAreaElement).value
    const pos = (e.target as HTMLTextAreaElement).selectionStart
    const before = val.slice(0, pos)
    if (before.endsWith('{{')) setShowAc(true)
    else setShowAc(false)
  }

  function insertScope(scope: string) {
    if (!ref.current) return
    const pos = ref.current.selectionStart
    const next = value.slice(0, pos) + scope + value.slice(pos)
    onChange(next)
    setShowAc(false)
    setTimeout(() => { ref.current?.focus(); ref.current!.selectionStart = ref.current!.selectionEnd = pos + scope.length }, 0)
  }

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setShowAc(false)
    }
    document.addEventListener('mousedown', handleClick)
    return () => document.removeEventListener('mousedown', handleClick)
  }, [])

  return (
    <div style={{ position: 'relative' }}>
      <textarea
        ref={ref}
        className="config-input wfe-expr-input"
        rows={rows ?? 2}
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyUp={handleKeyDown}
        placeholder={placeholder}
        spellCheck={false}
      />
      {showAc && (
        <div className="wfe-ac-dropdown">
          <div className="wfe-ac-group">Scopes</div>
          {SCOPES.map(s => <div key={s} className="wfe-ac-item" onMouseDown={() => insertScope(s)}>{s}</div>)}
          <div className="wfe-ac-group">Filters</div>
          {FILTERS.map(f => <div key={f} className="wfe-ac-item" onMouseDown={() => insertScope(f)}>{f}</div>)}
        </div>
      )}
    </div>
  )
}
