import { useState } from 'react'
import type {
  ProjectContextSummary,
  ProjectContextDetail,
  ConfigSaveState,
} from '../types'
import { computeDiff } from '../utils'
import FieldHint from './FieldHint'

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
}: ProjectContextsViewProps) {
  const [showArchived, setShowArchived] = useState(false)

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
                                {v.reason && <div className="instr-version-reason">{v.reason}</div>}
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
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
