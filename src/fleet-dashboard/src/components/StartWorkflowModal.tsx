import { useState, useMemo } from 'react'
import type { AgentState, WorkflowTypeInfo } from '../types'
import { apiFetch } from '../utils'
import { parseSchema, formToJson, jsonToForm, initFormValues, SchemaField, useRepositories } from './WorkflowEditor/schemaUtils'
import type { FormValues, ParsedSchema } from './WorkflowEditor/schemaUtils'
import FieldHint from './FieldHint'

// ── Props ─────────────────────────────────────────────────────────────────────

interface StartWorkflowModalProps {
  workflowTypes: WorkflowTypeInfo[]
  agents: AgentState[]
  onClose: () => void
}

// ── Component ─────────────────────────────────────────────────────────────────

export default function StartWorkflowModal({ workflowTypes, agents, onClose }: StartWorkflowModalProps) {
  const [search, setSearch] = useState('')
  const [selectedType, setSelectedType] = useState<WorkflowTypeInfo | null>(null)
  const [formValues, setFormValues] = useState<FormValues>({})
  const [rawJson, setRawJson] = useState('{}')
  const [isRawMode, setIsRawMode] = useState(false)
  const [rawError, setRawError] = useState<string | null>(null)
  const [customWorkflowId, setCustomWorkflowId] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [submitState, setSubmitState] = useState<'idle' | 'pending' | 'success' | 'error'>('idle')
  const [submitMsg, setSubmitMsg] = useState('')

  const filteredTypes = useMemo(() => {
    const q = search.toLowerCase()
    if (!q) return workflowTypes
    return workflowTypes.filter(t =>
      t.name.toLowerCase().includes(q) ||
      t.description.toLowerCase().includes(q) ||
      t.namespace.toLowerCase().includes(q)
    )
  }, [workflowTypes, search])

  const sortedAgents = useMemo(() =>
    [...agents].sort((a, b) => a.agentName.localeCompare(b.agentName)),
    [agents]
  )

  const repos = useRepositories()

  const schema = useMemo(() => selectedType ? parseSchema(selectedType.inputSchema) : null, [selectedType])

  function selectType(t: WorkflowTypeInfo) {
    const s = parseSchema(t.inputSchema)
    setSelectedType(t)
    setSearch('')
    setIsRawMode(s === null) // null schema (UWE) → start in raw mode
    setRawJson('{}')
    setRawError(null)
    setFormValues(s?.hasFields ? initFormValues(s) : {})
    setConfirmed(false)
    setSubmitState('idle')
    setSubmitMsg('')
    setCustomWorkflowId('')
  }

  function handleToggleRaw() {
    if (!schema) return
    if (!isRawMode) {
      // switching to raw: serialize form → JSON
      setRawJson(formToJson(formValues, schema))
      setRawError(null)
      setIsRawMode(true)
    } else {
      // switching back to form: parse JSON → form
      const parsed = jsonToForm(rawJson, schema)
      if (!parsed) {
        setRawError('Invalid JSON — cannot switch to form view')
        return
      }
      setFormValues(parsed)
      setRawError(null)
      setIsRawMode(false)
    }
  }

  function buildInputJson(): string | null {
    if (!selectedType || !schema) return null // null schema → pass rawJson
    if (schema === null) {
      // UWE type
      return rawJson.replace(/\s/g, '') === '{}' ? null : rawJson
    }
    if (!schema.hasFields) return null // no input needed
    if (isRawMode) {
      return rawJson.replace(/\s/g, '') === '{}' ? null : rawJson
    }
    const json = formToJson(formValues, schema)
    return JSON.parse(json) && Object.keys(JSON.parse(json)).length === 0 ? null : json
  }

  // If null schema (UWE) the raw JSON is the input
  const effectiveInputJson = (() => {
    if (!selectedType) return null
    if (selectedType.inputSchema === null) {
      return rawJson.replace(/\s/g, '') === '{}' ? null : rawJson
    }
    if (!schema) return null
    if (!schema.hasFields) return null
    if (isRawMode) return rawJson.replace(/\s/g, '') === '{}' ? null : rawJson
    const json = formToJson(formValues, schema)
    try {
      return Object.keys(JSON.parse(json)).length === 0 ? null : json
    } catch {
      return json
    }
  })()

  async function handleSubmit() {
    if (!selectedType) return
    setSubmitState('pending')
    try {
      const body: Record<string, unknown> = {
        workflowType: selectedType.name,
        namespace: selectedType.namespace,
        taskQueue: selectedType.taskQueue,
      }
      if (customWorkflowId.trim()) body.workflowId = customWorkflowId.trim()
      if (effectiveInputJson) {
        try { body.input = JSON.parse(effectiveInputJson) }
        catch { setSubmitState('error'); setSubmitMsg('Invalid JSON in input'); return }
      } else {
        body.input = {}
      }

      const res = await apiFetch('/api/workflows/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
      const data = await res.json()
      if (res.ok) {
        setSubmitState('success')
        setSubmitMsg(`Started: ${data.workflowId}`)
      } else {
        setSubmitState('error')
        setSubmitMsg(data.error ?? 'Failed to start workflow')
      }
    } catch (e) {
      setSubmitState('error')
      setSubmitMsg(String(e))
    }
  }

  return (
    <div className="swf-overlay" onClick={onClose}>
      <div className="swf-modal" onClick={e => e.stopPropagation()}>

        {/* ── Header ── */}
        <div className="swf-header">
          <span className="swf-title">Start Workflow</span>
          <button className="swf-close" onClick={onClose}>✕</button>
        </div>

        <div className="swf-body">
          {/* ── Type selector ── */}
          <div className="swf-section-label">Workflow Type</div>
          {selectedType ? (
            <div className="swf-selected-type">
              <span className="swf-type-name">{selectedType.name}</span>
              <span className="wf-ns-badge">{selectedType.namespace}</span>
              <button className="swf-change-btn" onClick={() => setSelectedType(null)}>Change</button>
            </div>
          ) : (
            <>
              <input
                className="swf-search"
                placeholder="Search types…"
                value={search}
                onChange={e => setSearch(e.target.value)}
                autoFocus
              />
              <div className="swf-type-list">
                {filteredTypes.length === 0 && (
                  <div className="swf-type-empty">No matching workflow types.</div>
                )}
                {filteredTypes.map(t => (
                  <div
                    key={`${t.namespace}/${t.name}`}
                    className="swf-type-item"
                    onClick={() => selectType(t)}
                  >
                    <div className="swf-type-item-top">
                      <span className="swf-type-name">{t.name}</span>
                      <span className="wf-ns-badge">{t.namespace}</span>
                    </div>
                    <div className="swf-type-desc">{t.description}</div>
                  </div>
                ))}
              </div>
            </>
          )}

          {/* ── Input form (shown when type is selected) ── */}
          {selectedType && (
            <>
              <div className="swf-divider" />
              <div className="swf-section-label">Input</div>

              {/* No input required */}
              {schema !== null && !schema.hasFields && (
                <div className="swf-no-input">No input required for this workflow type.</div>
              )}

              {/* Smart form */}
              {schema !== null && schema.hasFields && !isRawMode && (
                <>
                  {Object.entries(schema.properties).map(([key, prop]) => (
                    <SchemaField
                      key={key}
                      fieldKey={key}
                      prop={prop}
                      value={formValues[key] ?? (prop.type === 'boolean' ? false : '')}
                      required={schema.required.includes(key)}
                      onChange={v => setFormValues(prev => ({ ...prev, [key]: v }))}
                      agents={sortedAgents}
                      repos={repos}
                    />
                  ))}
                  <button className="swf-toggle-raw" onClick={handleToggleRaw}>Switch to raw JSON ▸</button>
                </>
              )}

              {/* Raw JSON editor (UWE type or toggled to raw) */}
              {(selectedType.inputSchema === null || isRawMode) && (
                <>
                  <textarea
                    className="swf-textarea swf-raw-json"
                    rows={8}
                    value={rawJson}
                    onChange={e => { setRawJson(e.target.value); setRawError(null) }}
                    placeholder="{}"
                    spellCheck={false}
                  />
                  {rawError && <div className="swf-raw-error">{rawError}</div>}
                  {isRawMode && schema !== null && (
                    <button className="swf-toggle-raw" onClick={handleToggleRaw}>Switch to form ◂</button>
                  )}
                </>
              )}

              {/* Optional workflow ID */}
              <div className="swf-section-label swf-section-label-sm">Workflow ID (optional)</div>
              <FieldHint>If blank, auto-generated as <code>{'{type}-{timestamp}'}</code>. Set a custom ID when you need to reference this run later or ensure idempotency.</FieldHint>
              <input
                className="swf-input"
                placeholder={`${selectedType.name}-{timestamp} (auto-generated)`}
                value={customWorkflowId}
                onChange={e => setCustomWorkflowId(e.target.value)}
              />

              {/* Confirmation summary */}
              {confirmed && submitState !== 'success' && (
                <div className="swf-confirm-section">
                  <div className="swf-confirm-summary">
                    <div><strong>Type:</strong> {selectedType.name}</div>
                    <div><strong>Namespace:</strong> {selectedType.namespace} / {selectedType.taskQueue}</div>
                    {customWorkflowId && <div><strong>ID:</strong> {customWorkflowId}</div>}
                    {effectiveInputJson && (
                      <div>
                        <strong>Input:</strong>
                        <pre className="swf-confirm-json">{effectiveInputJson}</pre>
                      </div>
                    )}
                  </div>
                  {submitState === 'error' && (
                    <div className="swf-error">{submitMsg}</div>
                  )}
                </div>
              )}

              {submitState === 'success' && (
                <div className="swf-success">
                  <div>{submitMsg}</div>
                </div>
              )}
            </>
          )}
        </div>

        {/* ── Footer (pinned action buttons) ── */}
        {selectedType && submitState !== 'success' && !confirmed && (
          <div className="swf-footer">
            <div className="swf-actions">
              <button
                className="swf-btn-cancel"
                onClick={onClose}
              >Cancel</button>
              <button
                className="swf-btn-confirm"
                onClick={() => setConfirmed(true)}
              >Review & Start ▸</button>
            </div>
          </div>
        )}

        {selectedType && submitState !== 'success' && confirmed && (
          <div className="swf-footer">
            <div className="swf-actions">
              <button className="swf-btn-cancel" onClick={() => setConfirmed(false)}>
                ◂ Back
              </button>
              <button
                className="swf-btn-start"
                disabled={submitState === 'pending'}
                onClick={handleSubmit}
              >
                {submitState === 'pending' ? '…' : 'Start Workflow'}
              </button>
            </div>
          </div>
        )}

        {selectedType && submitState === 'success' && (
          <div className="swf-footer">
            <div className="swf-actions">
              <button className="swf-btn-cancel" onClick={() => {
                setSelectedType(null)
                setConfirmed(false)
                setSubmitState('idle')
                setSubmitMsg('')
              }}>Start Another</button>
              <button className="swf-btn-confirm" onClick={onClose}>Done</button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
