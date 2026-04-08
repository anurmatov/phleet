import { useState, useEffect, useCallback, useRef } from 'react'
import type { AnyStep, StepPath, ViewMode, ValidationError } from './editorTypes'
import { deserializeStep, serializeStep, ensureIds } from './treeUtils'
import { makeStep } from './stepDefaults'
import { validateTree } from './validation'
import { apiFetch } from '../../utils'
import StepPalette from './StepPalette'
import StepCanvas from './StepCanvas'
import JsonPreview from './JsonPreview'
import StepConfigPanel from './StepConfigPanel'
import type { StepType } from './editorTypes'
import type { WorkflowTypeInfo } from '../../types'

interface WorkflowDefinitionEditorProps {
  /** null = new definition */
  defName: string | null
  onBack: () => void
  onSaved: (name: string) => void
  workflowTypes?: WorkflowTypeInfo[]
  namespaces?: string[]
}

interface DefMeta {
  namespace: string
  taskQueue: string
  description: string
  version: number
  versions: Array<{ version: number; definition: string; reason?: string; createdAt?: string }>
}

export default function WorkflowDefinitionEditor({ defName, onBack, onSaved, workflowTypes = [], namespaces = [] }: WorkflowDefinitionEditorProps) {
  const isNew = defName === null

  const [root, setRoot] = useState<AnyStep>(() => ensureIds(makeStep('sequence')))
  const [meta, setMeta] = useState<DefMeta>({ namespace: 'fleet', taskQueue: 'fleet', description: '', version: 0, versions: [] })
  const [newName, setNewName] = useState('')
  const [selectedPath, setSelectedPath] = useState<StepPath>([])
  const [viewMode, setViewMode] = useState<ViewMode>('visual')
  const [isDirty, setIsDirty] = useState(false)
  const [errors, setErrors] = useState<ValidationError[]>([])
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [saveMsg, setSaveMsg] = useState('')
  const [loading, setLoading] = useState(!isNew)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const validationTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Load definition
  useEffect(() => {
    if (isNew) return
    setLoading(true)
    apiFetch(`/api/workflow-definitions/${defName}`)
      .then(r => r.json())
      .then(data => {
        const parsedRoot = deserializeStep(data.definition)
        setRoot(parsedRoot)
        setMeta({
          namespace: data.namespace,
          taskQueue: data.taskQueue,
          description: data.description ?? '',
          version: data.version,
          versions: data.versions ?? [],
        })
        setErrors(validateTree(parsedRoot))
        setIsDirty(false)
      })
      .catch((e: unknown) => {
        setLoadError(e instanceof Error ? e.message : 'Failed to load definition')
      })
      .finally(() => setLoading(false))
  }, [defName, isNew])

  // Debounced validation
  const scheduleValidation = useCallback((newRoot: AnyStep) => {
    if (validationTimer.current) clearTimeout(validationTimer.current)
    validationTimer.current = setTimeout(() => setErrors(validateTree(newRoot)), 300)
  }, [])

  function handleChange(newRoot: AnyStep) {
    setRoot(newRoot)
    setIsDirty(true)
    scheduleValidation(newRoot)
  }

  async function handleSave() {
    const blockingErrors = errors.filter(e => e.blocking)
    if (blockingErrors.length > 0) return
    setSaveState('saving')
    try {
      const definition = serializeStep(root)
      const name = isNew ? newName.trim() : defName!
      if (isNew) {
        const res = await apiFetch('/api/workflow-definitions', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, namespace: meta.namespace, taskQueue: meta.taskQueue, description: meta.description, definition }),
        })
        if (!res.ok) throw new Error(await res.text())
        setSaveState('success')
        setSaveMsg('Created!')
        setIsDirty(false)
        onSaved(name)
      } else {
        const res = await apiFetch(`/api/workflow-definitions/${defName}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ definition, description: meta.description, namespace: meta.namespace, taskQueue: meta.taskQueue }),
        })
        if (!res.ok) throw new Error(await res.text())
        const updated = await res.json()
        setMeta(m => ({ ...m, version: updated.version ?? m.version + 1 }))
        setSaveState('success')
        setSaveMsg(`Saved v${meta.version + 1}`)
        setIsDirty(false)
        setTimeout(() => setSaveState('idle'), 3000)
      }
    } catch (e) {
      setSaveState('error')
      setSaveMsg(e instanceof Error ? e.message : 'Save failed')
      setTimeout(() => setSaveState('idle'), 5000)
    }
  }

  function handleAddRootStep(type: StepType) {
    // Append to root's steps (root is always a sequence-like container)
    const newRoot = {
      ...root,
      steps: [...(root.steps ?? []), ensureIds(makeStep(type))],
    }
    handleChange(newRoot)
  }

  const hasBlockingErrors = errors.some(e => e.blocking)

  if (loading) return <div className="wfe-loading">Loading…</div>
  if (loadError) return (
    <div className="wfe-editor">
      <div className="wfe-topbar">
        <button className="wfe-back-btn" onClick={onBack}>← Definitions</button>
      </div>
      <div className="wfe-load-error">
        <span className="wfe-load-error-icon">✕</span>
        <span>Failed to load definition: {loadError}</span>
      </div>
    </div>
  )

  return (
    <div className="wfe-editor">
      {/* Top bar */}
      <div className="wfe-topbar">
        <button className="wfe-back-btn" onClick={onBack}>← Definitions</button>
        <div className="wfe-topbar-meta">
          {isNew ? (
            <input
              className="wfe-name-input"
              placeholder="workflow name"
              value={newName}
              onChange={e => setNewName(e.target.value)}
            />
          ) : (
            <span className="wfe-def-name">{defName}</span>
          )}
          <select
            className="wfe-meta-select"
            value={meta.namespace}
            onChange={e => {
              const ns = e.target.value
              setMeta(m => ({ ...m, namespace: ns, taskQueue: ns }))
              setIsDirty(true)
            }}
          >
            {namespaces.length === 0
              ? <option value={meta.namespace}>{meta.namespace}</option>
              : namespaces.map(ns => <option key={ns} value={ns}>{ns}</option>)
            }
          </select>
          <select
            className="wfe-meta-select mono"
            value={meta.taskQueue}
            onChange={e => {
              setMeta(m => ({ ...m, taskQueue: e.target.value }))
              setIsDirty(true)
            }}
          >
            {namespaces.length === 0
              ? <option value={meta.taskQueue}>{meta.taskQueue}</option>
              : namespaces.map(ns => <option key={ns} value={ns}>{ns}</option>)
            }
          </select>
          {!isNew && <span className="wfe-meta-pill">v{meta.version}</span>}
          {isDirty && <span className="wfe-dirty-dot">● unsaved</span>}
        </div>
        <div className="wfe-topbar-actions">
          {/* View mode toggle */}
          <div className="wfe-view-toggle">
            {(['visual', 'json', 'split'] as ViewMode[]).map(m => (
              <button key={m} className={`wfe-view-btn${viewMode === m ? ' active' : ''}`} onClick={() => setViewMode(m)}>{m}</button>
            ))}
          </div>
          <button className="wfe-history-btn" onClick={() => setHistoryOpen(h => !h)}>History</button>
          <button
            className={`wfe-save-btn${saveState === 'success' ? ' success' : saveState === 'error' ? ' error' : ''}`}
            disabled={saveState === 'saving' || hasBlockingErrors || (isNew && !newName.trim())}
            onClick={handleSave}
          >
            {saveState === 'saving' ? '…' : saveState === 'success' ? saveMsg : saveState === 'error' ? saveMsg : isNew ? 'Create' : `Save v${meta.version + 1}`}
          </button>
        </div>
      </div>

      {/* Metadata row: description */}
      <div className="wfe-meta-row">
        <div className="wfe-meta-field wfe-meta-field-grow">
          <label className="wfe-meta-label">Description</label>
          <input
            className="config-input wfe-meta-input"
            placeholder="optional description"
            value={meta.description}
            onChange={e => { setMeta(m => ({ ...m, description: e.target.value })); setIsDirty(true) }}
          />
        </div>
      </div>

      {/* Validation banner */}
      {errors.length > 0 && (
        <div className="wfe-validation-banner">
          {errors.map((err, i) => (
            <div key={i} className={`wfe-validation-item${err.blocking ? ' blocking' : ' warning'}`}>
              {err.blocking ? '✕' : '⚠'} [{err.pathStr}] {err.message}
            </div>
          ))}
        </div>
      )}

      {/* Main layout */}
      <div className="wfe-main">
        {/* Palette */}
        <div className="wfe-palette-panel">
          <StepPalette onAddStep={handleAddRootStep} />
        </div>

        {/* Canvas area */}
        <div className="wfe-center">
          {(viewMode === 'visual' || viewMode === 'split') && (
            <div className="wfe-canvas-area">
              <StepCanvas
                root={root}
                selectedPath={selectedPath}
                errors={errors}
                onChange={handleChange}
                onSelect={setSelectedPath}
              />
            </div>
          )}
          {(viewMode === 'json' || viewMode === 'split') && (
            <div className="wfe-json-area">
              <JsonPreview root={root} />
            </div>
          )}
        </div>

        {/* Config panel */}
        <div className="wfe-config-panel-wrap">
          <StepConfigPanel
            root={root}
            selectedPath={selectedPath}
            onChange={handleChange}
            workflowTypes={workflowTypes}
          />
        </div>
      </div>

      {/* History drawer */}
      {historyOpen && (
        <HistoryDrawer
          versions={meta.versions}
          currentVersion={meta.version}
          onClose={() => setHistoryOpen(false)}
          onLoad={defJson => {
            const parsed = deserializeStep(defJson)
            setRoot(parsed)
            setIsDirty(true)
            scheduleValidation(parsed)
            setHistoryOpen(false)
          }}
        />
      )}
    </div>
  )
}

// ─── History drawer ───────────────────────────────────────────────────────────

interface HistoryDrawerProps {
  versions: Array<{ version: number; definition: string; reason?: string; createdAt?: string }>
  currentVersion: number
  onClose: () => void
  onLoad: (json: string) => void
}

function HistoryDrawer({ versions, currentVersion, onClose, onLoad }: HistoryDrawerProps) {
  const [viewing, setViewing] = useState<number | null>(null)

  return (
    <div className="wfe-history-drawer">
      <div className="wfe-history-header">
        <span>Version History</span>
        <button className="wfe-history-close" onClick={onClose}>✕</button>
      </div>
      {versions.length === 0 && <div className="wfe-history-empty">No previous versions.</div>}
      {versions.map(v => (
        <div key={v.version} className="wfe-history-item">
          <div className="wfe-history-item-header">
            <span className="wfe-history-ver">v{v.version}{v.version === currentVersion ? ' (current)' : ''}</span>
            <span className="wfe-history-date">{v.createdAt ? new Date(v.createdAt).toLocaleString() : ''}</span>
          </div>
          {v.reason && <div className="wfe-history-reason">{v.reason}</div>}
          <div className="wfe-history-actions">
            <button className="wfe-btn-sm" onClick={() => setViewing(viewing === v.version ? null : v.version)}>
              {viewing === v.version ? 'Hide' : 'View JSON'}
            </button>
            <button className="wfe-btn-sm" onClick={() => onLoad(v.definition)}>Load</button>
          </div>
          {viewing === v.version && (
            <pre className="wfe-history-json">{v.definition}</pre>
          )}
        </div>
      ))}
    </div>
  )
}
