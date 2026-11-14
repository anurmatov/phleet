import { useState } from 'react'
import type { InstructionsState } from '../hooks/useInstructions'
import { computeDiff } from '../utils'
import FieldHint from './FieldHint'
import SizeMeter from './SizeMeter'
import SizeCellView from './SizeCellView'
import { sizeCell, type MeterLimit } from '../sizeMeter'
import MemoryText from './MemoryText'
import Icon from './Icon'

interface InstructionsViewProps {
  /** The page's state and requests (hooks/useInstructions.ts). */
  state: InstructionsState
  /** #346 — the instruction soft limit from GET /api/prompt-size-policy; undefined when unavailable. */
  sizeLimit: MeterLimit
}

export default function InstructionsView({ state, sizeLimit }: InstructionsViewProps) {
  const {
    items: instructions,
    loading: instructionsLoading,
    expanded: expandedInstruction,
    detail: instructionDetail,
    detailLoading: instructionDetailLoading,
    edits: instructionEdits,
    reasons: instructionReason,
    saveState: instructionSaveState,
    saveMsg: instructionSaveMsg,
    selectedVersion,
    rollbackConfirm,
    deployConfirm,
    deployState,
    deployMsg,
    toggleConfirm: instrToggleConfirm,
    toggleState: instrToggleState,
    toggleMsg: instrToggleMsg,
    showNew: showNewForm,
    newForm,
    newState: newFormState,
    newMsg: newFormMsg,
    toggleExpanded: onToggleInstruction,
    setEdit: onSetEdits,
    setReason: onSetReason,
    save: onSave,
    selectVersion: onSelectVersion,
    rollbackClick: onRollbackClick,
    deployClick: onDeployClick,
    toggleActive: onToggleActive,
    toggleConfirmClick: onToggleConfirmClick,
    setShowNew: onShowNewForm,
    changeNewForm: onNewFormChange,
    create: onNewFormSubmit,
    reload: onRefresh,
    sizeWarnings,
  } = state
  const [showArchived, setShowArchived] = useState(false)

  const activeInstructions = instructions.filter(i => i.isActive)
  const inactiveInstructions = instructions.filter(i => !i.isActive)
  const displayed = showArchived ? instructions : activeInstructions

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Instructions
          {instructions.length > 0 && (
            <span className="section-count">
              {activeInstructions.length} active · {inactiveInstructions.length} inactive
            </span>
          )}
        </h1>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {inactiveInstructions.length > 0 && (
            <button
              className="wfd-cancel-btn"
              onClick={() => setShowArchived(s => !s)}
            >
              {showArchived ? 'Hide archived' : `Show archived (${inactiveInstructions.length})`}
            </button>
          )}
          <button className="icon-btn completed-refresh-btn" onClick={onRefresh} disabled={instructionsLoading} title="Refresh instructions" aria-label="Refresh instructions"><Icon name="refresh" /></button>
          <button className={`btn view-page-action${showNewForm ? '' : ' btn-primary'}`} onClick={() => onShowNewForm(!showNewForm)}>
            {showNewForm ? 'Cancel' : <><Icon name="plus" size={16} />New instruction</>}
          </button>
        </div>
      </div>

      {/* New instruction inline form */}
      {showNewForm && (
        <div className="wfd-new-form">
          <div className="wfd-new-form-title">New Instruction</div>
          <div className="wfd-new-form-fields">
            <div className="wfd-form-row">
              <label className="config-label">Name <span className="wfd-required">*</span></label>
              <FieldHint>Kebab-case identifier used to assign this instruction to agents (e.g. <code>co-cto</code>). Alphanumeric, hyphens, underscores only.</FieldHint>
              <input
                className="config-input"
                placeholder="e.g. my-instruction"
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
                placeholder="Instruction content…"
                value={newForm.content}
                onChange={e => onNewFormChange('content', e.target.value)}
              />
              <SizeMeter text={newForm.content} limit={sizeLimit} kind="prompt" />
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Reason</label>
              <FieldHint>Short note explaining this version (e.g. <code>tighten review checklist</code>). Shown in version history for audit trail.</FieldHint>
              <input
                className="config-input"
                placeholder="optional"
                value={newForm.reason}
                onChange={e => onNewFormChange('reason', e.target.value)}
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

      {instructionsLoading && <div className="view-empty">Loading…</div>}
      {!instructionsLoading && displayed.length === 0 && (
        <div className="view-empty">
          {instructions.length === 0
            ? 'No instructions found. DB may not be configured.'
            : 'No active instructions. Use "Show archived" to see inactive ones.'}
        </div>
      )}

      <div className="instructions-list">
        {displayed.map(instr => {
          const isOpen = expandedInstruction === instr.name
          const detail = instructionDetail[instr.name]
          const detailLoading = instructionDetailLoading[instr.name] ?? false
          const editContent = instructionEdits[instr.name] ?? ''
          const reason = instructionReason[instr.name] ?? ''
          const saveState = instructionSaveState[instr.name] ?? 'idle'
          const saveMsg = instructionSaveMsg[instr.name] ?? ''
          const selVer = selectedVersion[instr.name] ?? null
          const currentContent = detail?.versions.find(v => v.versionNumber === detail.currentVersion)?.content ?? ''
          const selVerContent = selVer !== null ? (detail?.versions.find(v => v.versionNumber === selVer)?.content ?? '') : ''
          const diffLines = selVer !== null && selVerContent ? computeDiff(selVerContent, currentContent) : null
          const toggleConfirming = instrToggleConfirm[instr.name] ?? false
          const toggleState = instrToggleState[instr.name] ?? 'idle'
          const toggleMsg = instrToggleMsg[instr.name] ?? ''
          const sizeWarning = sizeWarnings[instr.name]

          return (
            <div key={instr.name} className={`instr-row${!instr.isActive ? ' wfd-row-inactive' : ''}`}>
              <div className="instr-header" onClick={() => onToggleInstruction(instr.name)}>
                <span className="instr-name">{instr.name}</span>
                <span className="instr-meta">
                  <span className="instr-version">v{instr.currentVersion}</span>
                  <span className="instr-total">{instr.totalVersions} versions</span>
                  <span className={`wfd-active-badge${instr.isActive ? ' active' : ' inactive'}`}>
                    {instr.isActive ? 'active' : 'inactive'}
                  </span>
                  {sizeWarning && <span className="size-warning-badge" title={sizeWarning}>⚠</span>}
                  {instr.agents.length > 0 && (
                    <span className="instr-agents" title={instr.agents.join(', ')}>
                      {instr.agents.length} agent{instr.agents.length !== 1 ? 's' : ''}
                    </span>
                  )}
                  <SizeCellView cell={sizeCell(instr.currentBytes, sizeLimit)} />
                </span>
                <div className="wfd-row-actions" onClick={e => e.stopPropagation()}>
                  {toggleState === 'idle' || toggleState === 'pending' ? (
                    <button
                      className={`wfd-toggle-btn${toggleConfirming ? ' confirming' : ''}`}
                      disabled={toggleState === 'pending'}
                      onClick={() => {
                        if (toggleConfirming) {
                          onToggleActive(instr.name, !instr.isActive)
                        } else {
                          onToggleConfirmClick(instr.name)
                        }
                      }}
                    >
                      {toggleState === 'pending'
                        ? '…'
                        : toggleConfirming
                          ? `confirm ${instr.isActive ? 'disable' : 'enable'}?`
                          : instr.isActive ? 'disable' : 'enable'}
                    </button>
                  ) : (
                    <span className={`config-feedback config-feedback-${toggleState}`}>{toggleMsg}</span>
                  )}
                </div>
                <span className={`history-toggle${isOpen ? ' open' : ''}`}><Icon name="chevronDown" size={18} /></span>
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
                          onChange={e => onSetEdits(instr.name, e.target.value)}
                          rows={20}
                          spellCheck={false}
                        />
                        <SizeMeter text={editContent} limit={sizeLimit} kind="prompt" />
                        <div className="instr-save-row">
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <FieldHint>Short note explaining this version. Shown in version history for audit trail.</FieldHint>
                            <input
                              className="config-input instr-reason-input"
                              placeholder="reason (optional)"
                              value={reason}
                              onChange={e => onSetReason(instr.name, e.target.value)}
                              style={{ width: '100%' }}
                            />
                          </div>
                          <button
                            className="config-save-btn"
                            disabled={saveState === 'saving' || editContent === currentContent}
                            onClick={() => onSave(instr.name)}
                          >
                            {saveState === 'saving' ? '…' : 'Save'}
                          </button>
                          {(saveState === 'success' || saveState === 'error') && (
                            <span className={`config-feedback config-feedback-${saveState}`}>{saveMsg}</span>
                          )}
                        </div>
                        {sizeWarning && <div className="size-warning-notice">⚠ {sizeWarning}</div>}
                      </div>

                      <div className="instr-history-col">
                        <div className="config-label" style={{ marginBottom: 4 }}>Version history</div>
                        <div className="instr-version-list">
                          {detail.versions.map(v => {
                            const rbKey = `${instr.name}:${v.versionNumber}`
                            const isRbConfirming = rollbackConfirm[rbKey] ?? false
                            return (
                              <div
                                key={v.versionNumber}
                                className={`instr-version-item${v.versionNumber === detail.currentVersion ? ' current' : ''}${selVer === v.versionNumber ? ' selected' : ''}`}
                                onClick={() => onSelectVersion(instr.name, selVer === v.versionNumber ? null : v.versionNumber)}
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
                                      onClick={e => onRollbackClick(e, instr.name, v.versionNumber, saveState)}
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

                                {instr.agents.length > 0 && (() => {
                                  const agentList = instr.agents
                                  if (agentList.length === 1) {
                                    const agentName = agentList[0]
                                    const deployKey = `${instr.name}:${v.versionNumber}:${agentName}`
                                    const isDeployConfirming = deployConfirm[deployKey] ?? false
                                    const ds = deployState[deployKey] ?? 'idle'
                                    const dm = deployMsg[deployKey] ?? ''
                                    return (
                                      <div className="instr-deploy-row" onClick={e => e.stopPropagation()}>
                                        {ds === 'idle' || ds === 'deploying' ? (
                                          <button
                                            className={`instr-deploy-btn${isDeployConfirming ? ' confirming' : ''}`}
                                            disabled={ds === 'deploying'}
                                            onClick={e => onDeployClick(e, instr.name, v.versionNumber, agentName)}
                                            title={isDeployConfirming ? 'Click again to confirm deploy' : `Deploy v${v.versionNumber} to ${agentName}`}
                                          >
                                            {ds === 'deploying' ? '…' : isDeployConfirming ? `confirm deploy to ${agentName}?` : `deploy to ${agentName}`}
                                          </button>
                                        ) : (
                                          <span className={`instr-deploy-feedback instr-deploy-${ds}`}>{dm}</span>
                                        )}
                                      </div>
                                    )
                                  } else {
                                    return (
                                      <div className="instr-deploy-row" onClick={e => e.stopPropagation()}>
                                        {agentList.map(agentName => {
                                          const deployKey = `${instr.name}:${v.versionNumber}:${agentName}`
                                          const isDeployConfirming = deployConfirm[deployKey] ?? false
                                          const ds = deployState[deployKey] ?? 'idle'
                                          const dm = deployMsg[deployKey] ?? ''
                                          return ds === 'success' || ds === 'error' ? (
                                            <span key={agentName} className={`instr-deploy-feedback instr-deploy-${ds}`}>{dm}</span>
                                          ) : (
                                            <button
                                              key={agentName}
                                              className={`instr-deploy-btn${isDeployConfirming ? ' confirming' : ''}`}
                                              disabled={ds === 'deploying'}
                                              onClick={e => onDeployClick(e, instr.name, v.versionNumber, agentName)}
                                              title={isDeployConfirming ? `Click again to confirm deploy to ${agentName}` : `Deploy v${v.versionNumber} to ${agentName}`}
                                            >
                                              {ds === 'deploying' ? '…' : isDeployConfirming ? `confirm → ${agentName}?` : `→ ${agentName}`}
                                            </button>
                                          )
                                        })}
                                      </div>
                                    )
                                  }
                                })()}
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
