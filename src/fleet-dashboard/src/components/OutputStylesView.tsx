import { useState, useEffect, useRef } from 'react'
import type { OutputStyleDetail } from '../types'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'

type ActionState = 'idle' | 'pending' | 'success' | 'error'

/** Stands in for the name until one is typed. */
const NEW_STYLE_PLACEHOLDER_NAME = 'my-style'

/**
 * The starting point for a new style, with its frontmatter `name:` already matching the name the
 * operator typed.
 *
 * Not cosmetic: the validator refuses a body whose frontmatter name disagrees with the row name,
 * because Claude Code matches on the frontmatter name and a mismatch is a style that silently does
 * not load. A fixed `name: my-style` in the template therefore means the FIRST save of every style
 * anyone actually names is rejected. Pre-filling here is client-side only — the save path still
 * sends the body exactly as typed.
 */
function newStyleTemplate(name: string): string {
  return `---
name: ${name.trim() || NEW_STYLE_PLACEHOLDER_NAME}
description: One line describing what this style is for.
keep-coding-instructions: true
---

# Register

`
}

interface OutputStylesViewProps {
  /** Style to open on arrival, from `#output-styles/<name>`. Empty opens nothing. */
  initialStyle?: string
  /** Called after a create, delete or save, so the sidenav badge and the agent picker refresh. */
  onStylesChanged?: () => void
}

export default function OutputStylesView({ initialStyle = '', onStylesChanged }: OutputStylesViewProps) {
  const [styles, setStyles] = useState<OutputStyleDetail[]>([])
  const [loading, setLoading] = useState(true)
  const [expanded, setExpanded] = useState<string | null>(null)

  // Per-style editor buffer, keyed by name. Seeded from the row on first expand so an unsaved
  // edit survives collapsing and reopening.
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [saveState, setSaveState] = useState<Record<string, ActionState>>({})
  const [saveMsg, setSaveMsg] = useState<Record<string, string>>({})

  const [deleteConfirm, setDeleteConfirm] = useState<Record<string, boolean>>({})
  const [deleteState, setDeleteState] = useState<Record<string, ActionState>>({})
  const [deleteMsg, setDeleteMsg] = useState<Record<string, string>>({})
  const confirmTimers = useRef<Record<string, ReturnType<typeof setTimeout>>>({})
  const deleteResetTimers = useRef<Record<string, ReturnType<typeof setTimeout>>>({})

  const [reprovState, setReprovState] = useState<Record<string, ActionState>>({})
  const [reprovMsg, setReprovMsg] = useState<Record<string, string>>({})

  const [showNewForm, setShowNewForm] = useState(false)
  const [newName, setNewName] = useState('')
  const [newBody, setNewBody] = useState(() => newStyleTemplate(''))
  const [newFormState, setNewFormState] = useState<ActionState>('idle')
  const [newFormMsg, setNewFormMsg] = useState('')

  // Keeps the template's frontmatter name in step with the Name field for as long as the body is
  // untouched. Once the operator edits the body it is theirs, and nothing here rewrites it.
  function handleNewNameChange(value: string) {
    setNewBody(prev => (prev === newStyleTemplate(newName) ? newStyleTemplate(value) : prev))
    setNewName(value)
  }

  function load() {
    setLoading(true)
    apiFetch('/api/output-styles')
      .then(r => r.ok ? r.json() : Promise.reject(r.status))
      .then((data: OutputStyleDetail[]) => setStyles(data))
      .catch(() => setStyles([]))
      .finally(() => setLoading(false))
  }

  useEffect(() => { load() }, [])
  useEffect(() => () => {
    Object.values(confirmTimers.current).forEach(clearTimeout)
    Object.values(deleteResetTimers.current).forEach(clearTimeout)
  }, [])

  // Arriving from the agent picker's "read <name>" link. The row is opened and scrolled to once
  // the list has loaded — landing on a collapsed list would be the same as landing on the page,
  // which is what the link was supposed to improve on.
  const openedInitial = useRef(false)
  useEffect(() => {
    if (openedInitial.current || !initialStyle || styles.length === 0) return
    if (!styles.some(s => s.name === initialStyle)) return
    openedInitial.current = true
    open(initialStyle)
    requestAnimationFrame(() => {
      document.getElementById(`output-style-${initialStyle}`)
        ?.scrollIntoView({ block: 'start', behavior: 'smooth' })
    })
  }, [initialStyle, styles])

  /** Expands a style, seeding its editor buffer from the row on first open. */
  function open(name: string) {
    setExpanded(name)
    setEdits(prev => (name in prev
      ? prev
      : { ...prev, [name]: styles.find(s => s.name === name)?.body ?? '' }))
  }

  function toggle(name: string) {
    if (expanded === name) { setExpanded(null); return }
    open(name)
  }

  // The server derives `description` from the body's frontmatter, so the error text it returns is
  // the only place the frontmatter rules are stated — surface it verbatim rather than a generic
  // "save failed".
  async function errorText(res: Response): Promise<string> {
    try {
      const data = await res.json()
      return data?.error ?? `HTTP ${res.status}`
    } catch {
      return `HTTP ${res.status}`
    }
  }

  async function handleSave(name: string) {
    setSaveState(prev => ({ ...prev, [name]: 'pending' }))
    try {
      const res = await apiFetch(`/api/output-styles/${encodeURIComponent(name)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ body: edits[name] ?? '' }),
      })
      if (!res.ok) throw new Error(await errorText(res))
      const data = await res.json()
      setSaveState(prev => ({ ...prev, [name]: 'success' }))
      setSaveMsg(prev => ({ ...prev, [name]: data?.message ?? 'Saved' }))
      load()
      // The description shown in the agent picker is derived from the body, so a save can change
      // it — App's copy would otherwise keep showing the pre-edit line.
      onStylesChanged?.()
      setTimeout(() => setSaveState(prev => ({ ...prev, [name]: 'idle' })), 4000)
    } catch (e) {
      setSaveState(prev => ({ ...prev, [name]: 'error' }))
      setSaveMsg(prev => ({ ...prev, [name]: e instanceof Error ? e.message : String(e) }))
    }
  }

  function handleDeleteClick(name: string) {
    if (deleteConfirm[name]) {
      handleDelete(name)
      return
    }
    setDeleteConfirm(prev => ({ ...prev, [name]: true }))
    confirmTimers.current[name] = setTimeout(
      () => setDeleteConfirm(prev => ({ ...prev, [name]: false })), 5000)
  }

  async function handleDelete(name: string) {
    clearTimeout(confirmTimers.current[name])
    setDeleteConfirm(prev => ({ ...prev, [name]: false }))
    setDeleteState(prev => ({ ...prev, [name]: 'pending' }))
    try {
      const res = await apiFetch(`/api/output-styles/${encodeURIComponent(name)}`, { method: 'DELETE' })
      // A 409 here is the guard doing its job — the message names the agents still assigned.
      if (!res.ok) throw new Error(await errorText(res))
      setDeleteState(prev => ({ ...prev, [name]: 'success' }))
      setDeleteMsg(prev => ({ ...prev, [name]: 'Deleted' }))
      if (expanded === name) setExpanded(null)
      load()
      onStylesChanged?.()
    } catch (e) {
      setDeleteState(prev => ({ ...prev, [name]: 'error' }))
      setDeleteMsg(prev => ({ ...prev, [name]: e instanceof Error ? e.message : String(e) }))
      // Back to idle so the row regains its Delete button. A 409 here is the guard refusing while
      // an agent is still assigned — the operator clears the style on those agents and tries
      // again, and without this reset the row has no button left to try with until a Refresh.
      clearTimeout(deleteResetTimers.current[name])
      deleteResetTimers.current[name] = setTimeout(
        () => setDeleteState(prev => ({ ...prev, [name]: 'idle' })), 8000)
    }
  }

  async function handleReprovision(styleName: string, agentName: string) {
    const key = `${styleName}:${agentName}`
    setReprovState(prev => ({ ...prev, [key]: 'pending' }))
    try {
      const res = await apiFetch(`/api/agents/${encodeURIComponent(agentName)}/reprovision`, { method: 'POST' })
      if (!res.ok) throw new Error(await errorText(res))
      setReprovState(prev => ({ ...prev, [key]: 'success' }))
      setReprovMsg(prev => ({ ...prev, [key]: `${agentName} reprovisioned` }))
      setTimeout(() => setReprovState(prev => ({ ...prev, [key]: 'idle' })), 4000)
    } catch (e) {
      setReprovState(prev => ({ ...prev, [key]: 'error' }))
      setReprovMsg(prev => ({ ...prev, [key]: e instanceof Error ? e.message : String(e) }))
      setTimeout(() => setReprovState(prev => ({ ...prev, [key]: 'idle' })), 5000)
    }
  }

  const nameValid = /^[a-zA-Z0-9_-]+$/.test(newName)

  async function handleCreate() {
    if (!nameValid || !newBody.trim()) return
    setNewFormState('pending')
    try {
      const res = await apiFetch('/api/output-styles', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: newName, body: newBody }),
      })
      if (!res.ok) throw new Error(await errorText(res))
      setNewFormState('success')
      setNewFormMsg('Created')
      setNewName('')
      setNewBody(newStyleTemplate(''))
      setShowNewForm(false)
      load()
      onStylesChanged?.()
      setTimeout(() => setNewFormState('idle'), 2000)
    } catch (e) {
      setNewFormState('error')
      setNewFormMsg(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Output Styles
          {styles.length > 0 && <span className="section-count">{styles.length}</span>}
        </h1>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          <button className="completed-refresh-btn" onClick={load} disabled={loading} title="Refresh output styles">↻</button>
          <button
            className="view-page-action"
            onClick={() => { setShowNewForm(s => !s); setNewFormState('idle') }}
          >
            {showNewForm ? 'Cancel' : '+ New Style'}
          </button>
        </div>
      </div>

      <FieldHint>
        An output style carries an agent's chat tone and register. Claude resolves it as a style
        file; Codex and Gemini get the same text inlined into their prompt.{' '}
        <strong>An edit here does not reach a running agent</strong> — the body is written at
        provision time, so reprovision each assigned agent to apply it.
      </FieldHint>

      {showNewForm && (
        <div className="wfd-new-form">
          <div className="wfd-new-form-title">New Output Style</div>
          <div className="wfd-new-form-fields">
            <div className="wfd-form-row">
              <label className="config-label">Name <span className="wfd-required">*</span></label>
              <FieldHint>
                Kebab-case identifier agents are assigned to (e.g. <code>fleet-messaging</code>).
                It must match the <code>name:</code> in the frontmatter below — Claude Code
                resolves a style by its frontmatter name, not by the file name.
              </FieldHint>
              <input
                className="config-input"
                placeholder="e.g. my-style"
                value={newName}
                onChange={e => handleNewNameChange(e.target.value)}
              />
              {newName && !nameValid && (
                <div className="wfd-field-error">No spaces or special characters except - _</div>
              )}
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Body <span className="wfd-required">*</span></label>
              <FieldHint>
                A Claude Code output style file: YAML frontmatter (<code>name</code>,{' '}
                <code>description</code>, optionally <code>keep-coding-instructions</code>) followed
                by the style body.
              </FieldHint>
              <textarea
                className="instr-editor"
                rows={14}
                value={newBody}
                onChange={e => setNewBody(e.target.value)}
                spellCheck={false}
              />
            </div>
          </div>
          <div className="wfd-new-form-actions">
            <button
              className="config-save-btn"
              disabled={newFormState === 'pending' || !newName || !nameValid || !newBody.trim()}
              onClick={handleCreate}
            >
              {newFormState === 'pending' ? '…' : 'Create'}
            </button>
            <button className="wfd-cancel-btn" onClick={() => setShowNewForm(false)}>Cancel</button>
            {(newFormState === 'success' || newFormState === 'error') && (
              <span className={`config-feedback config-feedback-${newFormState}`}>{newFormMsg}</span>
            )}
          </div>
        </div>
      )}

      {loading && <div className="view-empty">Loading…</div>}
      {!loading && styles.length === 0 && (
        <div className="view-empty">No output styles found. DB may not be configured.</div>
      )}

      <div className="instructions-list">
        {styles.map(style => {
          const isOpen = expanded === style.name
          const editBody = edits[style.name] ?? style.body
          const ss = saveState[style.name] ?? 'idle'
          const ds = deleteState[style.name] ?? 'idle'
          const inUse = style.agents.length > 0

          return (
            <div key={style.name} className="instr-row" id={`output-style-${style.name}`}>
              <div className="instr-header" onClick={() => toggle(style.name)}>
                <span className="instr-name">{style.name}</span>
                <span className="instr-meta">
                  {style.description && <span className="instr-total">{style.description}</span>}
                  <span className="instr-agents" title={style.agents.join(', ')}>
                    {inUse
                      ? `${style.agents.length} agent${style.agents.length !== 1 ? 's' : ''}`
                      : 'unused'}
                  </span>
                </span>
                <div className="wfd-row-actions" onClick={e => e.stopPropagation()}>
                  {ds === 'idle' || ds === 'pending' ? (
                    <button
                      className={`agent-delete-btn${deleteConfirm[style.name] ? ' confirming' : ''}`}
                      disabled={ds === 'pending' || inUse}
                      title={inUse
                        ? `In use by ${style.agents.join(', ')} — clear the style on those agents first`
                        : 'Delete this style'}
                      onClick={() => handleDeleteClick(style.name)}
                    >
                      {ds === 'pending' ? '…' : deleteConfirm[style.name] ? 'confirm delete?' : 'delete'}
                    </button>
                  ) : (
                    <span className={`config-feedback config-feedback-${ds === 'success' ? 'success' : 'error'}`}>
                      {deleteMsg[style.name]}
                    </span>
                  )}
                </div>
                <span className="history-toggle">{isOpen ? '▲' : '▼'}</span>
              </div>

              {isOpen && (
                <div className="instr-detail">
                  <div className="instr-body">
                    <div className="instr-editor-col">
                      <div className="config-label" style={{ marginBottom: 4 }}>
                        Style file — frontmatter included
                      </div>
                      <textarea
                        className="instr-editor"
                        value={editBody}
                        onChange={e => setEdits(prev => ({ ...prev, [style.name]: e.target.value }))}
                        rows={22}
                        spellCheck={false}
                      />
                      <div className="instr-save-row">
                        <button
                          className="config-save-btn"
                          disabled={ss === 'pending' || editBody === style.body}
                          onClick={() => handleSave(style.name)}
                        >
                          {ss === 'pending' ? '…' : 'Save'}
                        </button>
                        {editBody !== style.body && ss !== 'pending' && (
                          <button
                            className="wfd-cancel-btn"
                            onClick={() => setEdits(prev => ({ ...prev, [style.name]: style.body }))}
                          >
                            Revert
                          </button>
                        )}
                        {(ss === 'success' || ss === 'error') && (
                          <span className={`config-feedback config-feedback-${ss}`}>{saveMsg[style.name]}</span>
                        )}
                      </div>
                    </div>

                    <div className="instr-history-col">
                      <div className="config-label" style={{ marginBottom: 4 }}>
                        Agents on this style
                      </div>
                      {!inUse && <div className="view-empty">No agent uses this style.</div>}
                      {inUse && (
                        <>
                          <FieldHint>
                            Editing this style changes the voice of every agent listed here — but
                            only after each one is reprovisioned.
                          </FieldHint>
                          <div className="instr-version-list">
                            {style.agents.map(agentName => {
                              const key = `${style.name}:${agentName}`
                              const rs = reprovState[key] ?? 'idle'
                              return (
                                <div key={agentName} className="instr-version-item">
                                  <div className="instr-version-header">
                                    <span className="instr-version-num">{agentName}</span>
                                  </div>
                                  <div className="instr-deploy-row">
                                    {rs === 'success' || rs === 'error' ? (
                                      <span className={`instr-deploy-feedback instr-deploy-${rs}`}>
                                        {reprovMsg[key]}
                                      </span>
                                    ) : (
                                      <button
                                        className="instr-deploy-btn"
                                        disabled={rs === 'pending'}
                                        onClick={() => handleReprovision(style.name, agentName)}
                                        title={`Reprovision ${agentName} so this style takes effect`}
                                      >
                                        {rs === 'pending' ? '…' : 'reprovision'}
                                      </button>
                                    )}
                                  </div>
                                </div>
                              )
                            })}
                          </div>
                        </>
                      )}
                    </div>
                  </div>
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
