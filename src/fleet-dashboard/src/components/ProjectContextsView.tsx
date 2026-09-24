import { useState } from 'react'
import type {
  ProjectContextSummary,
  ProjectContextDetail,
  ProjectCardRejection,
  RouteSignalKind,
  ConfigSaveState,
} from '../types'
import { apiFetch, computeDiff } from '../utils'
import FieldHint from './FieldHint'
import MemoryText from './MemoryText'

const ROUTE_KINDS: RouteSignalKind[] = ['repo', 'workflow', 'chat']

const ROUTE_PLACEHOLDER: Record<RouteSignalKind, string> = {
  repo: 'owner/name, e.g. org/app',
  workflow: 'workflow type, e.g. ExampleWorkflow',
  chat: 'chat id, e.g. -100000000001',
}

/** The error text of a failed response, from its `{ error }` body when there is one. */
async function errorText(r: Response): Promise<string> {
  const b = await r.json().catch(() => ({}))
  return b?.error ?? `Error ${r.status}`
}

// ── Card panel ────────────────────────────────────────────────────────────────

interface CardPanelProps {
  name: string
  detail: ProjectContextDetail
  cardAssignments: string[]
  /** Unsaved editor text; undefined shows the current card. Owned by the view so it survives collapsing. */
  edit: string | undefined
  onEdit: (content: string | undefined) => void
  onChanged: (name: string) => void
}

function CardPanel({ name, detail, cardAssignments, edit, onEdit, onChanged }: CardPanelProps) {
  const [reason, setReason] = useState('')
  const [saveState, setSaveState] = useState<ConfigSaveState>('idle')
  const [saveMsg, setSaveMsg] = useState('')
  const [rejection, setRejection] = useState<ProjectCardRejection | null>(null)
  const [rollbackConfirm, setRollbackConfirm] = useState<number | null>(null)

  const card = detail.card ?? null
  const fullVersion = detail.currentVersion
  const currentCardContent = card?.versions.find(v => v.versionNumber === card.currentVersion)?.content ?? ''
  const content = edit ?? currentCardContent
  // Re-saving unchanged text is still a real write when the full context moved on: it declares
  // the card valid for the newer full version.
  const unchanged = card !== null && content === currentCardContent && card.basedOnFullVersion === fullVersion

  function finish(state: 'success' | 'error', msg: string) {
    setSaveState(state); setSaveMsg(msg)
    setTimeout(() => setSaveState('idle'), state === 'success' ? 6000 : 8000)
  }

  function save() {
    setSaveState('saving'); setRejection(null)
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/card/versions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // basedOnFullVersion is the full version shown on the button — the author states which
      // full context the card reflects; there is deliberately no server-side default.
      body: JSON.stringify({ content, basedOnFullVersion: fullVersion, reason: reason.trim() || undefined }),
    })
      .then(async r => {
        if (r.status === 400) {
          // The gate's rejection. The editor keeps the text so the author can add what is missing.
          const b: ProjectCardRejection = await r.json().catch(() => ({ error: `Error ${r.status}` }))
          setRejection(b)
          // The full reason is in the rejection block above the save row; say it only once.
          throw new Error('Card not saved — see above')
        }
        if (!r.ok) throw new Error(await errorText(r))
        return r.json() as Promise<{ message: string; version: number }>
      })
      .then(res => {
        onEdit(undefined); setReason('')
        finish('success', `Saved card v${res.version} — reprovision card-mode agents to apply`)
        onChanged(name)
      })
      .catch((err: Error) => finish('error', err.message))
  }

  function rollback(version: number) {
    setRollbackConfirm(null); setSaveState('saving'); setRejection(null)
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/card/rollback/${version}`, { method: 'POST' })
      .then(async r => { if (!r.ok) throw new Error(await errorText(r)); return r.json() })
      .then(() => {
        onEdit(undefined)
        finish('success', `Rolled back card to v${version} — reprovision card-mode agents to apply`)
        onChanged(name)
      })
      .catch((err: Error) => finish('error', err.message))
  }

  function onRollbackClick(e: React.MouseEvent, version: number) {
    e.stopPropagation()
    if (saveState === 'saving') return
    if (rollbackConfirm === version) { rollback(version); return }
    setRollbackConfirm(version)
    setTimeout(() => setRollbackConfirm(prev => prev === version ? null : prev), 5000)
  }

  return (
    <div className="ctx-panel">
      <div className="config-section-label">Card</div>
      <FieldHint>A compact summary resident for agents that hold this project in <code>card</code> mode; the full context is attached to their turns routed here. It must carry every keep marker of the current full context. Card changes reach agents on reprovision.</FieldHint>
      <div className="instr-body">
        <div className="instr-editor-col">
          {card ? (
            <div className="instr-meta" style={{ marginLeft: 0, flexWrap: 'wrap' }}>
              <span className="ctx-card-badge">card v{card.currentVersion}</span>
              <span className="instr-total">written for full v{card.basedOnFullVersion} · full is v{fullVersion}</span>
              {card.stale && <span className="ctx-stale-badge" title="The card was written for an older full version. It is still used until you save it for the current one.">stale</span>}
              {cardAssignments.length > 0 && (
                <span className="instr-agents" title={cardAssignments.join(', ')}>
                  card mode: {cardAssignments.join(', ')}
                </span>
              )}
            </div>
          ) : (
            <div className="config-hint-text">No card yet. Assignments can only switch to card mode once one exists.</div>
          )}
          {card && card.missingKeeps.length > 0 && (
            <div className="ctx-warn">⚠ Missing keep markers: {card.missingKeeps.join(', ')} — card-mode agents get the full context until the card carries them.</div>
          )}
          {card && card.invalidKeeps.length > 0 && (
            <div className="ctx-warn">⚠ Invalid keep markers in the card (ignored): {card.invalidKeeps.join(', ')}</div>
          )}
          <textarea
            className="instr-editor"
            value={content}
            onChange={e => onEdit(e.target.value)}
            rows={12}
            spellCheck={false}
            placeholder="Card content…"
          />
          {rejection && (
            <div className="ctx-rejection">
              <div>{rejection.error}</div>
              {rejection.missingKeeps && rejection.missingKeeps.length > 0 && (
                <div>Missing keep markers: <code>{rejection.missingKeeps.join(', ')}</code></div>
              )}
              {rejection.invalidKeeps && rejection.invalidKeeps.length > 0 && (
                <div>Invalid keep markers: <code>{rejection.invalidKeeps.join(' · ')}</code></div>
              )}
            </div>
          )}
          <div className="instr-save-row">
            <div style={{ flex: 1, minWidth: 0 }}>
              <input
                className="config-input instr-reason-input"
                placeholder="reason (optional)"
                value={reason}
                onChange={e => setReason(e.target.value)}
                style={{ width: '100%' }}
              />
            </div>
            <button
              className="config-save-btn"
              disabled={saveState === 'saving' || !content.trim() || unchanged}
              onClick={save}
            >
              {saveState === 'saving' ? '…' : `Save card for full v${fullVersion}`}
            </button>
            {(saveState === 'success' || saveState === 'error') && (
              <span className={`config-feedback config-feedback-${saveState}`}>{saveMsg}</span>
            )}
          </div>
        </div>

        <div className="instr-history-col">
          <div className="config-label" style={{ marginBottom: 4 }}>Card version history</div>
          {!card && <div className="config-hint-text">No versions.</div>}
          {card && (
            <div className="instr-version-list">
              {card.versions.map(v => {
                const isCurrent = v.versionNumber === card.currentVersion
                const confirming = rollbackConfirm === v.versionNumber
                return (
                  <div
                    key={v.versionNumber}
                    className={`instr-version-item${isCurrent ? ' current' : ''}`}
                    onClick={() => { if (!isCurrent) onEdit(v.content) }}
                    title={isCurrent ? undefined : 'Click to load this version into the editor'}
                  >
                    <div className="instr-version-header">
                      <span className="instr-version-num">v{v.versionNumber}</span>
                      <span className="instr-total">for full v{v.basedOnFullVersion}</span>
                      {isCurrent && <span className="instr-current-badge">current</span>}
                      {!isCurrent && (
                        <button
                          className={`instr-rollback-btn${confirming ? ' confirming' : ''}`}
                          disabled={saveState === 'saving'}
                          onClick={e => onRollbackClick(e, v.versionNumber)}
                          title={confirming ? 'Click again to confirm rollback' : `Rollback card to v${v.versionNumber}`}
                        >
                          {confirming ? 'confirm rollback?' : 'rollback'}
                        </button>
                      )}
                    </div>
                    <div className="instr-version-meta">
                      <span>{v.createdAt}</span>
                      {v.createdBy && <span>{v.createdBy}</span>}
                    </div>
                    {v.reason && <div className="instr-version-reason"><MemoryText>{v.reason}</MemoryText></div>}
                  </div>
                )
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Routes panel ──────────────────────────────────────────────────────────────

function RoutesPanel({ name, detail, onChanged }: { name: string; detail: ProjectContextDetail; onChanged: (name: string) => void }) {
  const [kind, setKind] = useState<RouteSignalKind>('repo')
  const [value, setValue] = useState('')
  const [state, setState] = useState<ConfigSaveState>('idle')
  const [msg, setMsg] = useState('')
  const routes = detail.routes ?? []

  function finish(next: 'success' | 'error', text: string) {
    setState(next); setMsg(text)
    setTimeout(() => setState('idle'), next === 'success' ? 4000 : 8000)
  }

  function add() {
    setState('saving')
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/routes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, value: value.trim() }),
    })
      .then(async r => { if (!r.ok) throw new Error(await errorText(r)); return r.json() })
      .then(() => { setValue(''); finish('success', 'Route added — reprovision agents holding this project to apply'); onChanged(name) })
      .catch((err: Error) => finish('error', err.message))
  }

  function remove(id: number) {
    setState('saving')
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/routes/${id}`, { method: 'DELETE' })
      .then(async r => { if (!r.ok) throw new Error(await errorText(r)) })
      .then(() => { finish('success', 'Route removed — reprovision agents holding this project to apply'); onChanged(name) })
      .catch((err: Error) => finish('error', err.message))
  }

  return (
    <div className="ctx-panel">
      <div className="config-section-label">Routes</div>
      <FieldHint>Which turns get this project's full context attached, for agents holding it in <code>card</code> mode. <code>repo</code> = a workflow delegation's repo (<code>owner/name</code>, case-insensitive); <code>workflow</code> = the exact workflow type of a directive; <code>chat</code> = the chat id of a direct or group message. Precedence is repo &gt; workflow &gt; chat — the first level with a match wins. Routes reach agents on reprovision.</FieldHint>
      {routes.length === 0 && <div className="config-hint-text">No routes. Card-mode agents can still fetch the full context on demand.</div>}
      {routes.map(rt => (
        <div key={rt.id} className="config-related-row">
          <span className="ctx-route-kind">{rt.kind}</span>
          <span className="ctx-route-value">{rt.value}</span>
          <button className="config-remove-btn" disabled={state === 'saving'} onClick={() => remove(rt.id)} title="Remove route">✕</button>
        </div>
      ))}
      <div className="config-related-row">
        <select className="config-input config-input-sm" value={kind} onChange={e => setKind(e.target.value as RouteSignalKind)}>
          {ROUTE_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
        </select>
        <input
          className="config-input config-input-url"
          value={value}
          onChange={e => setValue(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && value.trim() && state !== 'saving') add() }}
          placeholder={ROUTE_PLACEHOLDER[kind]}
        />
        <button className="config-add-btn" disabled={state === 'saving' || !value.trim()} onClick={add}>+ add route</button>
      </div>
      {(state === 'success' || state === 'error') && (
        <span className={`config-feedback config-feedback-${state}`}>{msg}</span>
      )}
    </div>
  )
}

interface ProjectContextsViewProps {
  contexts: ProjectContextSummary[]
  contextsLoading: boolean
  expandedContext: string | null
  contextDetail: Record<string, ProjectContextDetail>
  contextDetailLoading: Record<string, boolean>
  contextEdits: Record<string, string>
  contextReason: Record<string, string>
  contextSaveState: Record<string, ConfigSaveState>
  contextSaveMsg: Record<string, string>
  selectedVersion: Record<string, number | null>
  rollbackConfirm: Record<string, boolean>
  ctxToggleConfirm: Record<string, boolean>
  ctxToggleState: Record<string, 'idle' | 'pending' | 'success' | 'error'>
  ctxToggleMsg: Record<string, string>
  showNewForm: boolean
  newForm: { name: string; content: string }
  newFormState: 'idle' | 'saving' | 'success' | 'error'
  newFormMsg: string
  onToggleContext: (name: string) => void
  onSetEdits: (name: string, content: string) => void
  onSetReason: (name: string, reason: string) => void
  onSave: (name: string) => void
  onSelectVersion: (name: string, version: number | null) => void
  onRollbackClick: (e: React.MouseEvent, name: string, version: number, saveState: ConfigSaveState) => void
  onToggleActive: (name: string, isActive: boolean) => void
  onToggleConfirmClick: (name: string) => void
  onShowNewForm: (show: boolean) => void
  onNewFormChange: (field: string, value: string) => void
  onNewFormSubmit: () => void
  onRefresh: () => void
  /** Re-reads one context (and the list) after a card or route write, leaving the full editor alone. */
  onCardOrRoutesChanged: (name: string) => void
}

export default function ProjectContextsView({
  contexts,
  contextsLoading,
  expandedContext,
  contextDetail,
  contextDetailLoading,
  contextEdits,
  contextReason,
  contextSaveState,
  contextSaveMsg,
  selectedVersion,
  rollbackConfirm,
  ctxToggleConfirm,
  ctxToggleState,
  ctxToggleMsg,
  showNewForm,
  newForm,
  newFormState,
  newFormMsg,
  onToggleContext,
  onSetEdits,
  onSetReason,
  onSave,
  onSelectVersion,
  onRollbackClick,
  onToggleActive,
  onToggleConfirmClick,
  onShowNewForm,
  onNewFormChange,
  onNewFormSubmit,
  onRefresh,
  onCardOrRoutesChanged,
}: ProjectContextsViewProps) {
  const [showArchived, setShowArchived] = useState(false)
  // Unsaved card text per context. Held here rather than in the panel so collapsing a row does not
  // throw it away; an absent key means "show the current card".
  const [cardEdits, setCardEdits] = useState<Record<string, string>>({})

  function setCardEdit(name: string, content: string | undefined) {
    setCardEdits(prev => {
      const next = { ...prev }
      if (content === undefined) delete next[name]
      else next[name] = content
      return next
    })
  }

  const activeContexts = contexts.filter(c => c.isActive)
  const inactiveContexts = contexts.filter(c => !c.isActive)
  const displayed = showArchived ? contexts : activeContexts

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Project Contexts
          {contexts.length > 0 && (
            <span className="section-count">
              {activeContexts.length} active · {inactiveContexts.length} inactive
            </span>
          )}
        </h1>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {inactiveContexts.length > 0 && (
            <button
              className="wfd-cancel-btn"
              onClick={() => setShowArchived(s => !s)}
            >
              {showArchived ? 'Hide archived' : `Show archived (${inactiveContexts.length})`}
            </button>
          )}
          <button className="completed-refresh-btn" onClick={onRefresh} disabled={contextsLoading} title="Refresh project contexts">↻</button>
          <button className="view-page-action" onClick={() => onShowNewForm(!showNewForm)}>
            {showNewForm ? 'Cancel' : '+ New Project Context'}
          </button>
        </div>
      </div>

      {/* New project context inline form */}
      {showNewForm && (
        <div className="wfd-new-form">
          <div className="wfd-new-form-title">New Project Context</div>
          <div className="wfd-new-form-fields">
            <div className="wfd-form-row">
              <label className="config-label">Name <span className="wfd-required">*</span></label>
              <FieldHint>Kebab-case identifier used to assign this context to agents (e.g. <code>fleet</code>, <code>backend</code>). Alphanumeric, hyphens, underscores only.</FieldHint>
              <input
                className="config-input"
                placeholder="e.g. my-project"
                value={newForm.name}
                onChange={e => onNewFormChange('name', e.target.value)}
              />
              {newForm.name && !/^[a-zA-Z0-9_-]+$/.test(newForm.name) && (
                <div className="wfd-field-error">No spaces or special characters except - _</div>
              )}
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Content <span className="wfd-required">*</span></label>
              <textarea
                className="instr-editor"
                rows={8}
                placeholder="Project context content…"
                value={newForm.content}
                onChange={e => onNewFormChange('content', e.target.value)}
              />
            </div>
          </div>
          <div className="wfd-new-form-actions">
            <button
              className="config-save-btn"
              disabled={
                newFormState === 'saving' ||
                !newForm.name || !/^[a-zA-Z0-9_-]+$/.test(newForm.name) ||
                !newForm.content.trim()
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

      {contextsLoading && <div className="view-empty">Loading…</div>}
      {!contextsLoading && displayed.length === 0 && (
        <div className="view-empty">
          {contexts.length === 0
            ? 'No project contexts found. DB may not be configured.'
            : 'No active project contexts. Use "Show archived" to see inactive ones.'}
        </div>
      )}

      <div className="instructions-list">
        {displayed.map(ctx => {
          const isOpen = expandedContext === ctx.name
          const detail = contextDetail[ctx.name]
          const detailLoading = contextDetailLoading[ctx.name] ?? false
          const editContent = contextEdits[ctx.name] ?? ''
          const reason = contextReason[ctx.name] ?? ''
          const saveState = contextSaveState[ctx.name] ?? 'idle'
          const saveMsg = contextSaveMsg[ctx.name] ?? ''
          const selVer = selectedVersion[ctx.name] ?? null
          const currentContent = detail?.versions.find(v => v.versionNumber === detail.currentVersion)?.content ?? ''
          const selVerContent = selVer !== null ? (detail?.versions.find(v => v.versionNumber === selVer)?.content ?? '') : ''
          const diffLines = selVer !== null && selVerContent ? computeDiff(selVerContent, currentContent) : null
          const toggleConfirming = ctxToggleConfirm[ctx.name] ?? false
          const toggleState = ctxToggleState[ctx.name] ?? 'idle'
          const toggleMsg = ctxToggleMsg[ctx.name] ?? ''

          return (
            <div key={ctx.name} className={`instr-row${!ctx.isActive ? ' wfd-row-inactive' : ''}`}>
              <div className="instr-header" onClick={() => onToggleContext(ctx.name)}>
                <span className="instr-name">{ctx.name}</span>
                <span className="instr-meta">
                  <span className="instr-version">v{ctx.currentVersion}</span>
                  {ctx.cardVersion != null && (
                    <span
                      className="ctx-card-badge"
                      title={ctx.cardAssignments?.length
                        ? `card mode: ${ctx.cardAssignments.join(', ')}`
                        : 'No agent holds this project in card mode'}
                    >
                      card v{ctx.cardVersion}
                    </span>
                  )}
                  {ctx.cardStale && (
                    <span className="ctx-stale-badge" title="The card was written for an older full version">stale</span>
                  )}
                  <span className="instr-total">{ctx.totalVersions} versions</span>
                  <span className={`wfd-active-badge${ctx.isActive ? ' active' : ' inactive'}`}>
                    {ctx.isActive ? 'active' : 'inactive'}
                  </span>
                  {ctx.agents.length > 0 && (
                    <span className="instr-agents" title={ctx.agents.join(', ')}>
                      {ctx.agents.length} agent{ctx.agents.length !== 1 ? 's' : ''}
                    </span>
                  )}
                </span>
                <div className="wfd-row-actions" onClick={e => e.stopPropagation()}>
                  {toggleState === 'idle' || toggleState === 'pending' ? (
                    <button
                      className={`wfd-toggle-btn${toggleConfirming ? ' confirming' : ''}`}
                      disabled={toggleState === 'pending'}
                      onClick={() => {
                        if (toggleConfirming) {
                          onToggleActive(ctx.name, !ctx.isActive)
                        } else {
                          onToggleConfirmClick(ctx.name)
                        }
                      }}
                    >
                      {toggleState === 'pending'
                        ? '…'
                        : toggleConfirming
                          ? `confirm ${ctx.isActive ? 'disable' : 'enable'}?`
                          : ctx.isActive ? 'disable' : 'enable'}
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
                      <div className="instr-editor-col">
                        <div className="config-label" style={{ marginBottom: 4 }}>
                          Current content (v{detail.currentVersion})
                        </div>
                        <textarea
                          className="instr-editor"
                          value={editContent}
                          onChange={e => onSetEdits(ctx.name, e.target.value)}
                          rows={20}
                          spellCheck={false}
                        />
                        <div className="instr-save-row">
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <FieldHint>Short note explaining this version. Shown in version history.</FieldHint>
                            <input
                              className="config-input instr-reason-input"
                              placeholder="reason (optional)"
                              value={reason}
                              onChange={e => onSetReason(ctx.name, e.target.value)}
                              style={{ width: '100%' }}
                            />
                          </div>
                          <button
                            className="config-save-btn"
                            disabled={saveState === 'saving' || editContent === currentContent}
                            onClick={() => onSave(ctx.name)}
                          >
                            {saveState === 'saving' ? '…' : 'Save'}
                          </button>
                          {(saveState === 'success' || saveState === 'error') && (
                            <span className={`config-feedback config-feedback-${saveState}`}>{saveMsg}</span>
                          )}
                        </div>
                      </div>

                      <div className="instr-history-col">
                        <div className="config-label" style={{ marginBottom: 4 }}>Version history</div>
                        <div className="instr-version-list">
                          {detail.versions.map(v => {
                            const rbKey = `${ctx.name}:${v.versionNumber}`
                            const isRbConfirming = rollbackConfirm[rbKey] ?? false
                            return (
                              <div
                                key={v.versionNumber}
                                className={`instr-version-item${v.versionNumber === detail.currentVersion ? ' current' : ''}${selVer === v.versionNumber ? ' selected' : ''}`}
                                onClick={() => onSelectVersion(ctx.name, selVer === v.versionNumber ? null : v.versionNumber)}
                              >
                                <div className="instr-version-header">
                                  <span className="instr-version-num">v{v.versionNumber}</span>
                                  {v.versionNumber === detail.currentVersion && (
                                    <span className="instr-current-badge">current</span>
                                  )}
                                  {v.versionNumber !== detail.currentVersion && (
                                    <button
                                      className={`instr-rollback-btn${isRbConfirming ? ' confirming' : ''}`}
                                      disabled={saveState === 'saving'}
                                      onClick={e => onRollbackClick(e, ctx.name, v.versionNumber, saveState)}
                                      title={isRbConfirming ? 'Click again to confirm rollback' : `Rollback to v${v.versionNumber}`}
                                    >
                                      {isRbConfirming ? 'confirm rollback?' : 'rollback'}
                                    </button>
                                  )}
                                </div>
                                <div className="instr-version-meta">
                                  <span>{v.createdAt}</span>
                                  {v.createdBy && <span>{v.createdBy}</span>}
                                </div>
                                {v.reason && <div className="instr-version-reason"><MemoryText>{v.reason}</MemoryText></div>}
                              </div>
                            )
                          })}
                        </div>

                        {diffLines && selVer !== null && (
                          <div className="instr-diff">
                            <div className="config-label" style={{ marginBottom: 4 }}>
                              diff: v{selVer} → v{detail.currentVersion}
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
                  {!detailLoading && detail && (
                    <>
                      <CardPanel
                        name={ctx.name}
                        detail={detail}
                        cardAssignments={ctx.cardAssignments ?? []}
                        edit={cardEdits[ctx.name]}
                        onEdit={content => setCardEdit(ctx.name, content)}
                        onChanged={onCardOrRoutesChanged}
                      />
                      <RoutesPanel name={ctx.name} detail={detail} onChanged={onCardOrRoutesChanged} />
                    </>
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
