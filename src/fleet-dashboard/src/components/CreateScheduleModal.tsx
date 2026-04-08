import { useState, useMemo } from 'react'
import type { WorkflowTypeInfo } from '../types'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'

interface CreateScheduleModalProps {
  workflowTypes: WorkflowTypeInfo[]
  namespaces: string[]
  onClose: () => void
  onCreated: () => void
}

// Basic cron validation: 5 fields, each a digit/*/range/step
const CRON_RE = /^(\*|[0-9,\-*/]+)\s+(\*|[0-9,\-*/]+)\s+(\*|[0-9,\-*/]+)\s+(\*|[0-9,\-*/]+)\s+(\*|[0-9,\-*/]+)$/

export default function CreateScheduleModal({ workflowTypes, namespaces, onClose, onCreated }: CreateScheduleModalProps) {
  const [search, setSearch] = useState('')
  const [selectedType, setSelectedType] = useState<WorkflowTypeInfo | null>(null)
  const [namespace, setNamespace] = useState('fleet')
  const [scheduleId, setScheduleId] = useState('')
  const [cronExpression, setCronExpression] = useState('')
  const [inputJson, setInputJson] = useState('{}')
  const [memo, setMemo] = useState('')
  const [paused, setPaused] = useState(false)
  const [autoScheduleId, setAutoScheduleId] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [submitState, setSubmitState] = useState<'idle' | 'pending' | 'success' | 'error'>('idle')
  const [submitMsg, setSubmitMsg] = useState('')

  const filteredTypes = useMemo(() => {
    const q = search.toLowerCase()
    if (!q) return workflowTypes
    return workflowTypes.filter(t =>
      t.name.toLowerCase().includes(q) ||
      t.namespace.toLowerCase().includes(q)
    )
  }, [workflowTypes, search])

  function selectType(t: WorkflowTypeInfo) {
    setSelectedType(t)
    setNamespace(t.namespace)
    setAutoScheduleId(`${t.name.toLowerCase().replace(/_/g, '-')}-${Math.random().toString(36).slice(2, 10)}`)
    setConfirmed(false)
    setSubmitState('idle')
    setSubmitMsg('')
  }

  const cronError = cronExpression.trim() && !CRON_RE.test(cronExpression.trim())
    ? 'Invalid cron expression (expected 5 fields)'
    : null

  const effectiveScheduleId = scheduleId.trim() || autoScheduleId

  async function handleSubmit() {
    if (!selectedType || !cronExpression.trim()) return
    setSubmitState('pending')
    try {
      const body: Record<string, unknown> = {
        workflowType:   selectedType.name,
        namespace,
        taskQueue:      selectedType.taskQueue || namespace,
        cronExpression: cronExpression.trim(),
        paused,
      }
      if (scheduleId.trim())  body.scheduleId = scheduleId.trim()
      if (memo.trim())        body.memo = memo.trim()
      const trimmedInput = inputJson.replace(/\s/g, '')
      if (trimmedInput && trimmedInput !== '{}') {
        try { JSON.parse(inputJson); body.inputJson = inputJson }
        catch { setSubmitState('error'); setSubmitMsg('Input is not valid JSON'); return }
      }

      const res = await apiFetch('/api/schedules', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
      const data = await res.json()
      if (res.ok) {
        setSubmitState('success')
        setSubmitMsg(`Created: ${data.scheduleId}`)
      } else {
        setSubmitState('error')
        setSubmitMsg(data.error ?? 'Failed to create schedule')
      }
    } catch (e) {
      setSubmitState('error')
      setSubmitMsg(String(e))
    }
  }

  return (
    <div className="swf-overlay" onClick={onClose}>
      <div className="swf-modal" onClick={e => e.stopPropagation()}>

        <div className="swf-header">
          <span className="swf-title">Create Schedule</span>
          <button className="swf-close" onClick={onClose}>✕</button>
        </div>

        <div className="swf-body">
          {/* Workflow type selector */}
          <div className="swf-section-label">Workflow Type</div>
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
                className={`swf-type-item${selectedType?.name === t.name && selectedType.namespace === t.namespace ? ' selected' : ''}`}
                onClick={() => selectType(t)}
              >
                <div className="swf-type-item-top">
                  <span className="swf-type-name">{t.name}</span>
                  <span className="wf-ns-badge">{t.namespace}</span>
                </div>
              </div>
            ))}
          </div>

          {selectedType && (
            <>
              <div className="swf-divider" />

              {/* Namespace */}
              <div className="swf-section-label">Namespace</div>
              <select
                className="swf-input"
                value={namespace}
                onChange={e => setNamespace(e.target.value)}
              >
                {namespaces.map(ns => <option key={ns} value={ns}>{ns}</option>)}
              </select>

              {/* Cron expression */}
              <div className="swf-section-label">Cron Expression *</div>
              <div className="swf-cron-presets">
                {[
                  { label: 'Every hour',        cron: '0 * * * *'   },
                  { label: 'Every 4h',          cron: '0 */4 * * *' },
                  { label: 'Daily 06:00',       cron: '0 6 * * *'   },
                  { label: 'Daily 08:00',       cron: '0 8 * * *'   },
                  { label: 'Weekly Mon 06:00',  cron: '0 6 * * 1'   },
                  { label: 'Monthly 1st 09:00', cron: '0 9 1 * *'   },
                ].map(p => (
                  <button
                    key={p.cron}
                    className="swf-cron-preset"
                    type="button"
                    onClick={() => setCronExpression(p.cron)}
                  >{p.label}</button>
                ))}
              </div>
              <input
                className="swf-input"
                placeholder="0 9 * * * (daily at 09:00 UTC)"
                value={cronExpression}
                onChange={e => setCronExpression(e.target.value)}
              />
              {cronError && <div className="swf-raw-error">{cronError}</div>}
              <div className="swf-field-desc" style={{ marginBottom: 8 }}>
                Standard 5-field cron (min hour dom month dow). Use CRON_TZ= prefix for timezones.
              </div>

              {/* Schedule ID */}
              <div className="swf-section-label swf-section-label-sm">Schedule ID (optional)</div>
              <input
                className="swf-input"
                placeholder={autoScheduleId}
                value={scheduleId}
                onChange={e => setScheduleId(e.target.value)}
              />

              {/* Input JSON */}
              <div className="swf-section-label swf-section-label-sm">Input JSON (optional)</div>
              <FieldHint>JSON object passed as input to each workflow run. Must match the workflow's input schema. Use <code>{'{}'}</code> for workflows with no required input.</FieldHint>
              <textarea
                className="swf-textarea swf-raw-json"
                rows={4}
                value={inputJson}
                onChange={e => setInputJson(e.target.value)}
                placeholder="{}"
                spellCheck={false}
              />

              {/* Memo */}
              <div className="swf-section-label swf-section-label-sm">Memo (optional)</div>
              <FieldHint>Human-readable label shown in the schedules list. Not used by the workflow itself.</FieldHint>
              <input
                className="swf-input"
                placeholder="Human-readable description"
                value={memo}
                onChange={e => setMemo(e.target.value)}
              />

              {/* Start paused */}
              <div className="swf-field" style={{ flexDirection: 'column', gap: 4 }}>
                <div style={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8 }}>
                  <input
                    id="sched-paused"
                    type="checkbox"
                    checked={paused}
                    onChange={e => setPaused(e.target.checked)}
                  />
                  <label className="swf-label" htmlFor="sched-paused" style={{ margin: 0 }}>Start paused</label>
                </div>
                <FieldHint>Creates the schedule in a paused state. Useful when you want to configure a schedule without triggering it immediately.</FieldHint>
              </div>

              {/* Actions */}
              {!confirmed && submitState !== 'success' && (
                <div className="swf-actions">
                  <button className="swf-btn-cancel" onClick={onClose}>Cancel</button>
                  <button
                    className="swf-btn-confirm"
                    disabled={!cronExpression.trim() || !!cronError}
                    onClick={() => setConfirmed(true)}
                  >
                    Review ▸
                  </button>
                </div>
              )}

              {confirmed && submitState !== 'success' && (
                <div className="swf-confirm-section">
                  <div className="swf-confirm-summary">
                    <div><strong>Type:</strong> {selectedType.name}</div>
                    <div><strong>Namespace:</strong> {namespace} / {selectedType.taskQueue || namespace}</div>
                    <div><strong>Schedule ID:</strong> {effectiveScheduleId}</div>
                    <div><strong>Cron:</strong> {cronExpression}</div>
                    {memo && <div><strong>Memo:</strong> {memo}</div>}
                    {paused && <div><strong>Starts paused</strong></div>}
                    {inputJson.replace(/\s/g, '') !== '{}' && (
                      <div><strong>Input:</strong><pre className="swf-confirm-json">{inputJson}</pre></div>
                    )}
                  </div>
                  <div className="swf-actions">
                    <button className="swf-btn-cancel" onClick={() => setConfirmed(false)}>◂ Back</button>
                    <button
                      className="swf-btn-start"
                      disabled={submitState === 'pending'}
                      onClick={handleSubmit}
                    >
                      {submitState === 'pending' ? '…' : 'Create Schedule'}
                    </button>
                  </div>
                  {submitState === 'error' && <div className="swf-error">{submitMsg}</div>}
                </div>
              )}

              {submitState === 'success' && (
                <div className="swf-success">
                  <div>{submitMsg}</div>
                  <div className="swf-actions">
                    <button className="swf-btn-cancel" onClick={() => {
                      setSelectedType(null); setConfirmed(false)
                      setSubmitState('idle'); setSubmitMsg('')
                      setCronExpression(''); setScheduleId(''); setMemo('')
                      setInputJson('{}'); setPaused(false)
                    }}>Create Another</button>
                    <button className="swf-btn-confirm" onClick={onCreated}>Done</button>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  )
}
