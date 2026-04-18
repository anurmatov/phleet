import { useState } from 'react'
import type { CreateForm, InstructionSummary } from '../types'
import { PROVIDER_DEFAULT_MODEL } from '../constants'
import ModelSelector from './ModelSelector'
import FieldHint from './FieldHint'
import InstructionPicker from './InstructionPicker'

interface CreateAgentModalProps {
  createForm: CreateForm
  createState: 'idle' | 'creating' | 'success' | 'error'
  createMsg: string
  agentNames: string[]
  copyFrom: string
  copyFromLoading: boolean
  allInstructions: InstructionSummary[]
  templateName?: string
  onCopyFrom: (name: string) => void
  onFormChange: (patch: Partial<CreateForm>) => void
  onSubmit: () => void
  onClose: () => void
}

export default function CreateAgentModal({
  createForm,
  createState,
  createMsg,
  agentNames,
  copyFrom,
  copyFromLoading,
  allInstructions,
  templateName,
  onCopyFrom,
  onFormChange,
  onSubmit,
  onClose,
}: CreateAgentModalProps) {
  const [shortNameTouched, setShortNameTouched] = useState(false)

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal create-agent-modal" onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">New Agent</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          {templateName && (
            <p className="create-agent-template-note">
              Based on <strong>{templateName}</strong> template — customize as needed.
            </p>
          )}
          <div className="config-row">
            <label className="config-label">Copy from existing agent</label>
            <FieldHint>Pre-fills all fields from an existing agent's config — tools, projects, MCP endpoints, networks, and Telegram config are all copied.</FieldHint>
            <select
              className="config-input"
              value={copyFrom}
              onChange={e => onCopyFrom(e.target.value)}
              disabled={copyFromLoading}
            >
              <option value="">— start from defaults —</option>
              {agentNames.map(n => (
                <option key={n} value={n}>{n}</option>
              ))}
            </select>
            {copyFromLoading && <span className="config-feedback" style={{ marginLeft: 8 }}>loading…</span>}
          </div>

          <div className="config-row">
            <label className="config-label">Name <span style={{ color: 'var(--red)' }}>*</span></label>
            <FieldHint>Unique identifier used as the container name prefix and DB key. Lowercase, no spaces (e.g. <code>anew</code>).</FieldHint>
            <input
              className="config-input"
              value={createForm.name}
              onChange={e => {
                const val = e.target.value
                onFormChange(shortNameTouched ? { name: val } : { name: val, shortName: val })
              }}
              placeholder="e.g. anew"
            />
          </div>
          <div className="config-row">
            <label className="config-label">Short name</label>
            <FieldHint>Used for heartbeat routing and Telegram commands. Defaults to Name — only set this if you need a different routing key.</FieldHint>
            <input
              className="config-input"
              value={createForm.shortName}
              onChange={e => { setShortNameTouched(true); onFormChange({ shortName: e.target.value }) }}
              placeholder="auto-filled from name"
            />
          </div>
          <div className="config-row">
            <label className="config-label">Display Name</label>
            <input className="config-input" value={createForm.displayName} onChange={e => onFormChange({ displayName: e.target.value })} placeholder="e.g. anew (auto-fills from name)" />
          </div>
          <div className="config-row">
            <label className="config-label">Role <span style={{ color: 'var(--red)' }}>*</span></label>
            <FieldHint>Maps to an instruction file in the roles directory (e.g. <code>developer</code> loads <code>roles/developer/system.md</code>).</FieldHint>
            <input className="config-input" value={createForm.role} onChange={e => onFormChange({ role: e.target.value })} placeholder="e.g. developer" />
          </div>
          <div className="config-row">
            <label className="config-label">Provider</label>
            <select
              className="config-input"
              value={createForm.provider || 'claude'}
              onChange={e => onFormChange({ provider: e.target.value, model: PROVIDER_DEFAULT_MODEL[e.target.value] ?? '' })}
            >
              <option value="claude">Claude (Anthropic)</option>
              <option value="codex">Codex (OpenAI)</option>
            </select>
          </div>
          <div className="config-row">
            <label className="config-label">
              Model{(createForm.provider || 'claude') !== 'codex' && <span style={{ color: 'var(--red)' }}> *</span>}
            </label>
            <ModelSelector
              provider={createForm.provider || 'claude'}
              value={createForm.model}
              onChange={model => onFormChange({ model })}
            />
          </div>
          <div className="config-row">
            <label className="config-label">Memory (MB)</label>
            <input className="config-input config-input-short" type="number" value={createForm.memoryLimitMb} onChange={e => onFormChange({ memoryLimitMb: e.target.value })} />
          </div>
          {allInstructions.some(i => i.isActive && i.name !== 'base') && (
            <div className="config-row">
              <label className="config-label">Instructions</label>
              <FieldHint>Role instructions to attach at creation. <code>base</code> is auto-attached. Load order is editable after creation via the config panel.</FieldHint>
              <InstructionPicker
                allInstructions={allInstructions}
                selected={createForm.instructions}
                onChange={instructions => onFormChange({ instructions })}
              />
            </div>
          )}
          <p className="create-agent-hint">
            {templateName
              ? <>The container will be <strong>provisioned automatically</strong> after creation. Use <strong>✎ config</strong> to adjust tools and MCP endpoints afterwards.</>
              : <>After creating, use the <strong>✎ config</strong> button to set up tools, projects, MCP endpoints, and Telegram config. Then click <strong>↻ Provision</strong> on the agent card to start the container.</>
            }
          </p>

          <div className="config-save-row">
            <button
              className="config-save-btn"
              disabled={createState === 'creating'}
              onClick={onSubmit}
            >
              {createState === 'creating' ? '…' : 'Create'}
            </button>
            {(createState === 'success' || createState === 'error') && (
              <span className={`config-feedback config-feedback-${createState}`}>{createMsg}</span>
            )}
            {createState === 'idle' && createMsg && (
              <span className="config-feedback config-feedback-error">{createMsg}</span>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
