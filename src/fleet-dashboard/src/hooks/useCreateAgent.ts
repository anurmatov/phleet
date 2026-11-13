import { useState } from 'react'
import type { AgentConfig, AgentState, CreateForm, McpEndpointEntry } from '../types'
import { apiGet, apiSend, errorMessage } from '../utils'
import { PROVIDER_DEFAULT_MODEL } from '../constants'
import { warmupTimeoutEditValue } from '../warmupTimeout'
import { DEFAULT_CREATE_FORM, createAgentBody, createFormFromConfig } from '../agentCreate'

export type CreateState = 'idle' | 'creating' | 'success' | 'error'

/** `GET /api/agent-templates/{name}`: the template's create-form defaults. */
interface AgentTemplateDetail {
  displayName?: string
  role?: string
  model?: string
  provider?: string
  memoryLimitMb?: number
  permissionMode?: string
  maxTurns?: number
  workDir?: string
  proactiveIntervalMinutes?: number
  groupListenMode?: string
  groupDebounceSeconds?: number
  warmupTimeoutSeconds?: number
  showStats?: boolean
  prefixMessages?: boolean
  formattingMode?: number
  suppressToolMessages?: boolean
  telegramSendOnly?: boolean
  autoMemoryEnabled?: boolean
  tools?: { toolName: string }[]
  projects?: string[]
  networks?: string[]
  envRefs?: string[]
  mcpEndpoints?: McpEndpointEntry[]
  instructions?: { name: string; loadOrder: number }[]
}

function formFromTemplate(t: AgentTemplateDetail): CreateForm {
  return {
    ...DEFAULT_CREATE_FORM,
    role: t.role ?? '',
    model: t.model ?? PROVIDER_DEFAULT_MODEL['claude'],
    provider: t.provider ?? 'claude',
    memoryLimitMb: String(t.memoryLimitMb ?? 4096),
    permissionMode: t.permissionMode ?? 'acceptEdits',
    maxTurns: String(t.maxTurns ?? 50),
    workDir: t.workDir ?? '/workspace',
    proactiveIntervalMinutes: String(t.proactiveIntervalMinutes ?? 0),
    groupListenMode: t.groupListenMode ?? 'mention',
    groupDebounceSeconds: String(t.groupDebounceSeconds ?? 15),
    warmupTimeoutSeconds: warmupTimeoutEditValue(t.warmupTimeoutSeconds),
    showStats: t.showStats ?? false,
    prefixMessages: t.prefixMessages ?? false,
    formattingMode: t.formattingMode ?? 0,
    suppressToolMessages: t.suppressToolMessages ?? false,
    telegramSendOnly: t.telegramSendOnly ?? false,
    autoMemoryEnabled: t.autoMemoryEnabled ?? true,
    tools: (t.tools ?? []).map(x => x.toolName).join('\n'),
    projects: (t.projects ?? []).join('\n'),
    networks: (t.networks ?? []).join('\n'),
    envRefs: (t.envRefs ?? []).join('\n'),
    mcpEndpoints: (t.mcpEndpoints ?? []).map(e => ({ mcpName: e.mcpName, url: e.url, transportType: e.transportType })),
    instructions: (t.instructions ?? []).map(i => ({ name: i.name, loadOrder: i.loadOrder })),
  }
}

/** The New Agent dialog: its form, where the form was filled from, and the create request. */
export interface CreateAgentState {
  isOpen: boolean
  form: CreateForm
  state: CreateState
  msg: string
  copyFrom: string
  copyFromLoading: boolean
  /** Set when the form came from a template: the agent is then provisioned on create. */
  templateName: string | undefined
  /** Opens an empty dialog, or one holding `form` (a `?name=` link). */
  open: (form?: CreateForm) => void
  openFromTemplate: (templateName: string) => Promise<void>
  close: () => void
  change: (patch: Partial<CreateForm>) => void
  /** Fills the form from an existing agent's config. */
  copy: (sourceName: string) => Promise<void>
  submit: () => Promise<void>
}

/** `agents` supplies a copied agent's role: the config route carries none, the agent list does. */
export function useCreateAgent(agents: Record<string, AgentState>): CreateAgentState {
  const [isOpen, setIsOpen] = useState(false)
  const [form, setForm] = useState<CreateForm>(DEFAULT_CREATE_FORM)
  const [state, setState] = useState<CreateState>('idle')
  const [msg, setMsg] = useState('')
  const [copyFrom, setCopyFrom] = useState('')
  const [copyFromLoading, setCopyFromLoading] = useState(false)
  const [templateName, setTemplateName] = useState<string | undefined>(undefined)

  function open(initial?: CreateForm) {
    setForm(initial ?? DEFAULT_CREATE_FORM)
    // A `?name=` link leaves the rest of the dialog as it was; the New Agent button starts it over.
    if (!initial) { setCopyFrom(''); setState('idle'); setMsg('') }
    setIsOpen(true)
  }

  async function openFromTemplate(name: string) {
    try {
      const t = await apiGet<AgentTemplateDetail>(`/api/agent-templates/${encodeURIComponent(name)}`)
      setTemplateName(t.displayName ?? name)
      setForm(formFromTemplate(t))
      setCopyFrom('')
      setState('idle')
      setMsg('')
      setIsOpen(true)
    } catch {/* ignore */}
  }

  async function copy(sourceName: string) {
    setCopyFrom(sourceName)
    if (!sourceName) return
    setCopyFromLoading(true)
    try {
      const cfg = await apiGet<AgentConfig>(`/api/agents/${encodeURIComponent(sourceName)}/config`)
      setForm(prev => createFormFromConfig(prev, cfg, agents[sourceName]?.role))
    } catch { /* the form keeps what it had */
    } finally {
      setCopyFromLoading(false)
    }
  }

  async function submit() {
    // The body is built in agentCreate.ts, where a test holds it against the create route.
    const create = createAgentBody(form, !!templateName)
    if (!create.ok) { setMsg(create.error); return }
    setState('creating'); setMsg('')
    try {
      const answer = await apiSend<{ message?: string } | null>('/api/agents', 'POST', create.body)
      // Apply instruction assignments if any were selected. Non-fatal (they can be set in the
      // config panel), but a refusal is said, not hidden behind "created".
      let instructionsNote = ''
      if (form.instructions.length > 0) {
        try {
          await apiSend(`/api/agents/${encodeURIComponent(form.name.trim())}/config`, 'PUT', {
            instructions: form.instructions.map(i => ({ instructionName: i.name, loadOrder: i.loadOrder })),
          })
        } catch (e) {
          instructionsNote = ` Instructions were not assigned (${errorMessage(e)}); set them in the config panel.`
        }
      }
      setState('success')
      const defaultMsg = templateName
        ? `✓ ${form.name.trim()} provisioned`
        : 'Agent created — configure and provision when ready'
      setMsg((answer?.message ?? defaultMsg) + instructionsNote)
      // A note about instructions stays up longer: it is the only place it is shown.
      setTimeout(() => {
        setIsOpen(false); setState('idle'); setMsg(''); setForm(DEFAULT_CREATE_FORM); setCopyFrom(''); setTemplateName(undefined)
      }, instructionsNote ? 8000 : 3000)
    } catch (e) {
      setState('error'); setMsg(errorMessage(e))
    }
  }

  return {
    isOpen, form, state, msg, copyFrom, copyFromLoading, templateName,
    open, openFromTemplate,
    close: () => { setIsOpen(false); setTemplateName(undefined) },
    change: patch => setForm(prev => ({ ...prev, ...patch })),
    copy, submit,
  }
}
