import { useState, useEffect } from 'react'
import type { AgentState, WorkflowTypeInfo } from '../../types'
import { apiFetch } from '../../utils'
import FieldHint from '../FieldHint'

// ── Repository types ────────────────────────────────────────────────────────────

export interface RepoInfo {
  name: string
  fullName: string
  isActive: boolean
}

// ── Schema helpers ─────────────────────────────────────────────────────────────

export interface SchemaProperty {
  type?: string
  description?: string
  items?: { type?: string }
}

export interface ParsedSchema {
  properties: Record<string, SchemaProperty>
  required: string[]
  hasFields: boolean
}

export type FormValues = Record<string, string | boolean>

export function parseSchema(inputSchema: string | null): ParsedSchema | null {
  if (inputSchema === null) return null
  try {
    const s = JSON.parse(inputSchema)
    const props: Record<string, SchemaProperty> = s.properties ?? {}
    return { properties: props, required: s.required ?? [], hasFields: Object.keys(props).length > 0 }
  } catch {
    return null
  }
}

export function initFormValues(schema: ParsedSchema): FormValues {
  const vals: FormValues = {}
  for (const [key, prop] of Object.entries(schema.properties)) {
    vals[key] = prop.type === 'boolean' ? false : ''
  }
  return vals
}

export function convertFieldValue(value: string | boolean, prop: SchemaProperty, key?: string): unknown {
  if (typeof value === 'string' && value.includes('{{')) return value  // template expression — keep as string
  if (value === '' || value === undefined || value === null) return undefined
  if (prop.type === 'integer' || prop.type === 'number') {
    const n = Number(value)
    return isNaN(n) ? value : n
  }
  if (prop.type === 'boolean') {
    return typeof value === 'boolean' ? value : value === 'true'
  }
  const isAgentsField = typeof key === 'string' && key.toLowerCase().endsWith('agents')
  if ((prop.type === 'array' && prop.items?.type === 'string') || isAgentsField) {
    const arr = String(value).split(',').map(s => s.trim()).filter(Boolean)
    return arr.length > 0 ? arr : undefined
  }
  if (typeof value === 'string' && !value.trim()) return undefined
  return value
}

export function formToJson(values: FormValues, schema: ParsedSchema): string {
  const result: Record<string, unknown> = {}
  for (const [key, prop] of Object.entries(schema.properties)) {
    const val = values[key]
    if (val === undefined || val === null) continue
    const converted = convertFieldValue(val, prop, key)
    if (converted !== undefined) result[key] = converted
  }
  return JSON.stringify(result, null, 2)
}

export function jsonToForm(json: string, schema: ParsedSchema): FormValues | null {
  try {
    const obj = JSON.parse(json)
    const vals: FormValues = {}
    for (const [key, prop] of Object.entries(schema.properties)) {
      const v = obj[key]
      if (v === undefined) {
        vals[key] = prop.type === 'boolean' ? false : ''
      } else if (prop.type === 'boolean') {
        vals[key] = Boolean(v)
      } else if (prop.type === 'array') {
        vals[key] = Array.isArray(v) ? v.join(', ') : String(v)
      } else {
        vals[key] = String(v)
      }
    }
    return vals
  } catch {
    return null
  }
}

// ── Agent pill ─────────────────────────────────────────────────────────────────

export function AgentPill({ agent, selected, onClick }: {
  agent: AgentState
  selected: boolean
  onClick: () => void
}) {
  const isRunning = agent.effectiveStatus === 'running'
  return (
    <button
      className={`swf-agent-pill${selected ? ' selected' : ''}`}
      onClick={onClick}
      type="button"
      title={agent.agentName}
    >
      <span className={`swf-agent-dot ${isRunning ? 'running' : 'stopped'}`} />
      <span className="swf-agent-display">{agent.displayName ?? agent.agentName}</span>
      <span className="swf-agent-name">{agent.agentName}</span>
    </button>
  )
}

// ── Repo picker ─────────────────────────────────────────────────────────────────

export function useRepositories(): RepoInfo[] {
  const [repos, setRepos] = useState<RepoInfo[]>([])
  useEffect(() => {
    apiFetch('/api/repositories')
      .then(r => r.json())
      .then((data: RepoInfo[]) => setRepos(data))
      .catch(() => {})
  }, [])
  return repos
}

function RepoPicker({ value, onChange, repos }: {
  value: string | boolean
  onChange: (v: string) => void
  repos: RepoInfo[]
}) {
  return (
    <div className="swf-repo-picker">
      <select
        className="swf-input"
        value={value as string}
        onChange={e => onChange(e.target.value)}
      >
        <option value="">— select repo —</option>
        {repos.map(r => (
          <option key={r.name} value={r.fullName}>{r.name} ({r.fullName})</option>
        ))}
      </select>
    </div>
  )
}

// ── Field renderer ─────────────────────────────────────────────────────────────

export function SchemaField({
  fieldKey, prop, value, required, onChange, agents, repos,
}: {
  fieldKey: string
  prop: SchemaProperty
  value: string | boolean
  required: boolean
  onChange: (v: string | boolean) => void
  agents: AgentState[]
  repos?: RepoInfo[]
}) {
  const label = fieldKey + (required ? ' *' : '')
  const id = `swf-${fieldKey}`

  // Template expression: always render as plain text input regardless of schema type
  const hasTemplate = typeof value === 'string' && value.includes('{{')

  const fkLower = fieldKey.toLowerCase()
  const isSingleAgentField = fkLower.endsWith('agent') && !fkLower.endsWith('agents')
  const isMultiAgentField = fkLower.endsWith('agents')
  const isRepoField = fkLower === 'repo' || fkLower.endsWith('repo')

  let input: React.ReactNode
  if (!hasTemplate && isRepoField && repos && repos.length > 0) {
    input = <RepoPicker value={value} onChange={v => onChange(v)} repos={repos} />
  } else if (!hasTemplate && isSingleAgentField && prop.type === 'string') {
    input = (
      <div className="swf-agent-pills">
        {agents.map(a => (
          <AgentPill
            key={a.agentName}
            agent={a}
            selected={value === a.agentName}
            onClick={() => onChange(value === a.agentName ? '' : a.agentName)}
          />
        ))}
      </div>
    )
  } else if (!hasTemplate && isMultiAgentField && (prop.type === 'array' || prop.type === 'string')) {
    const selected = new Set(
      String(value).split(',').map(s => s.trim()).filter(Boolean)
    )
    input = (
      <div className="swf-agent-pills">
        {agents.map(a => (
          <AgentPill
            key={a.agentName}
            agent={a}
            selected={selected.has(a.agentName)}
            onClick={() => {
              const next = new Set(selected)
              next.has(a.agentName) ? next.delete(a.agentName) : next.add(a.agentName)
              onChange(Array.from(next).join(', '))
            }}
          />
        ))}
      </div>
    )
  } else if (!hasTemplate && prop.type === 'boolean') {
    const boolVal = typeof value === 'boolean' ? value : value === 'true'
    input = (
      <input
        id={id}
        type="checkbox"
        checked={boolVal}
        onChange={e => onChange(e.target.checked)}
      />
    )
  } else if (!hasTemplate && (prop.type === 'integer' || prop.type === 'number')) {
    input = (
      <input
        id={id}
        type="number"
        className="swf-input"
        value={value as string}
        onChange={e => onChange(e.target.value)}
      />
    )
  } else if (!hasTemplate && prop.type === 'array' && prop.items?.type === 'string') {
    input = (
      <input
        id={id}
        type="text"
        className="swf-input"
        placeholder="comma-separated values"
        value={value as string}
        onChange={e => onChange(e.target.value)}
      />
    )
  } else if (!hasTemplate && (prop.type === 'object' || prop.type === undefined)) {
    input = (
      <textarea
        id={id}
        className="swf-textarea"
        rows={3}
        placeholder='{"key": "value"}'
        value={value as string}
        onChange={e => onChange(e.target.value)}
      />
    )
  } else {
    input = (
      <input
        id={id}
        type="text"
        className="swf-input"
        value={value as string}
        onChange={e => onChange(e.target.value)}
      />
    )
  }

  const isArrayInput = prop.type === 'array' && !isMultiAgentField
  return (
    <div className="swf-field">
      <label className="swf-label" htmlFor={id}>{label}</label>
      {input}
      {isArrayInput && !hasTemplate && (
        <FieldHint>Enter comma-separated values (e.g. <code>cto,developer,reviewer</code>).</FieldHint>
      )}
      {prop.description && <div className="swf-field-desc">{prop.description}</div>}
    </div>
  )
}

// ── Workflow type selector (combobox) ──────────────────────────────────────────

export function WorkflowTypeSelector({
  value,
  workflowTypes,
  onSelect,
  className,
  placeholder,
}: {
  value: string
  workflowTypes: WorkflowTypeInfo[]
  onSelect: (name: string, namespace?: string, taskQueue?: string) => void
  className?: string
  placeholder?: string
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState(value)

  // Sync query when value changes externally
  useEffect(() => { setQuery(value) }, [value])

  const filtered = workflowTypes.filter(t =>
    !query ||
    t.name.toLowerCase().includes(query.toLowerCase()) ||
    t.namespace.toLowerCase().includes(query.toLowerCase())
  )

  return (
    <div className="wts-wrapper">
      <input
        className={className ?? 'config-input'}
        value={query}
        placeholder={placeholder ?? 'workflow type'}
        onChange={e => {
          setQuery(e.target.value)
          setOpen(true)
        }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
      />
      {open && filtered.length > 0 && (
        <div className="wts-dropdown">
          {filtered.map(t => (
            <div
              key={`${t.namespace}/${t.name}`}
              className="wts-item"
              onMouseDown={() => {
                onSelect(t.name, t.namespace, t.taskQueue)
                setQuery(t.name)
                setOpen(false)
              }}
            >
              <span className="wts-name">{t.name}</span>
              <span className="wts-ns">{t.namespace}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
