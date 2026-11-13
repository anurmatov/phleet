import { useState } from 'react'
import type { AgentConfig, ConfigEdits, ConfigSaveState } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiGet, apiSend, errorMessage, firstFailure } from '../utils'
import { warmupTimeoutEditValue } from '../warmupTimeout'
import { contextWindowEditValue } from '../contextWindow'
import { localModelEditValues } from '../localModel'
import { configSaveBody } from '../agentConfigSave'

/** The agent config dialog: the stored config, the edits over it, the save, and memory project access. */
export interface AgentConfigState {
  /** The agent whose dialog is open, or null. */
  agent: string | null
  data: AgentConfig | null
  edits: ConfigEdits | null
  saveState: ConfigSaveState
  saveMsg: string
  loading: boolean
  reprovisionConfirm: boolean
  projectAccess: string[] | null
  projectAccessLoading: boolean
  /** Opens the dialog for the agent, or closes it when it is already that agent's. */
  open: (agentName: string) => void
  close: () => void
  change: (patch: Partial<ConfigEdits>) => void
  save: (andReprovision: boolean) => void
  toggleReprovisionConfirm: () => void
  addProjectAccess: (project: string) => Promise<void>
  removeProjectAccess: (project: string) => Promise<void>
  toggleProjectAccessWildcard: (enable: boolean) => Promise<void>
}

/** The dialog's fields for a stored config: numbers as text, lists joined, absent values blank. */
function editsFor(cfg: AgentConfig): ConfigEdits {
  return {
    model: cfg.model,
    memoryLimitMb: String(cfg.memoryLimitMb),
    isEnabled: cfg.isEnabled,
    image: cfg.image ?? '',
    permissionMode: cfg.permissionMode,
    maxTurns: String(cfg.maxTurns),
    workDir: cfg.workDir,
    proactiveIntervalMinutes: String(cfg.proactiveIntervalMinutes),
    groupListenMode: cfg.groupListenMode,
    groupDebounceSeconds: String(cfg.groupDebounceSeconds),
    warmupTimeoutSeconds: warmupTimeoutEditValue(cfg.warmupTimeoutSeconds),
    shortName: cfg.shortName,
    showStats: cfg.showStats,
    prefixMessages: cfg.prefixMessages,
    formattingMode: cfg.formattingMode,
    suppressToolMessages: cfg.suppressToolMessages ?? false,
    telegramSendOnly: cfg.telegramSendOnly,
    provider: cfg.provider ?? 'claude',
    codexSandboxMode: cfg.codexSandboxMode ?? '',
    effort: cfg.effort ?? '',
    jsonSchema: cfg.jsonSchema ?? '',
    agentsJson: cfg.agentsJson ?? '',
    autoMemoryEnabled: cfg.autoMemoryEnabled,
    journalEnabled: cfg.journalEnabled ?? false,
    journalCrossChatEnabled: cfg.journalCrossChatEnabled ?? false,
    chatEnabled: cfg.chatEnabled ?? false,
    hostPort: cfg.hostPort != null ? String(cfg.hostPort) : '',
    tools: cfg.tools.map(t => t.toolName).join(', '),
    projects: cfg.projects,
    mcpEndpoints: cfg.mcpEndpoints,
    networks: cfg.networks.join(', '),
    envRefs: cfg.envRefs.join(', '),
    telegramUsers: cfg.telegramUsers.join(', '),
    telegramGroups: cfg.telegramGroups.join(', '),
    canReceiveChatRequests: cfg.canReceiveChatRequests ?? false,
    requestReceivedMessage: cfg.requestReceivedMessage ?? '',
    mountDockerSock: cfg.mountDockerSock ?? false,
    outputStyle: cfg.outputStyle ?? '',
    ...localModelEditValues(cfg),
    contextWindow: contextWindowEditValue(cfg.contextWindow),
    harnessExtra: cfg.harnessExtra ?? '',
    instructions: cfg.instructions ?? [],
  }
}

const idList = (text: string) => text.split(',').map(s => s.trim()).filter(Boolean).map(Number).filter(n => !isNaN(n))

/**
 * `onOpen` runs each time a dialog opens, so the page can re-read the lists the dialog offers
 * (output styles, projects, instructions) in case they changed since it loaded.
 */
export function useAgentConfig(timers: KeyedTimers, onOpen: () => void): AgentConfigState {
  const [agent, setAgent] = useState<string | null>(null)
  const [data, setData] = useState<AgentConfig | null>(null)
  const [edits, setEdits] = useState<ConfigEdits | null>(null)
  const [saveState, setSaveState] = useState<ConfigSaveState>('idle')
  const [saveMsg, setSaveMsg] = useState('')
  const [loading, setLoading] = useState(false)
  const [reprovisionConfirm, setReprovisionConfirm] = useState(false)
  const [projectAccess, setProjectAccess] = useState<string[] | null>(null)
  const [projectAccessLoading, setProjectAccessLoading] = useState(false)

  /** Shows a result on the dialog's status line; it clears itself after `ms`. */
  function report(state: 'success' | 'error', message: string, ms: number) {
    setSaveState(state); setSaveMsg(message)
    timers.after('config-save', ms, () => setSaveState('idle'))
  }

  function open(agentName: string) {
    if (agent === agentName) {
      setAgent(null); setData(null); setEdits(null); setReprovisionConfirm(false)
      return
    }
    setAgent(agentName)
    setData(null); setEdits(null)
    setSaveState('idle'); setReprovisionConfirm(false)
    setProjectAccess(null)
    setLoading(true)
    setProjectAccessLoading(true)
    apiGet<{ projects: string[] }>(`/api/agents/${encodeURIComponent(agentName)}/project-access`)
      .then(answer => setProjectAccess(answer.projects))
      .catch(() => setProjectAccess([]))
      .finally(() => setProjectAccessLoading(false))
    onOpen()
    apiGet<AgentConfig>(`/api/agents/${encodeURIComponent(agentName)}/config`)
      .then(cfg => { setData(cfg); setEdits(editsFor(cfg)) })
      .catch(() => { setSaveMsg('Failed to load config'); setSaveState('error') })
      .finally(() => setLoading(false))
  }

  function close() {
    setAgent(null); setData(null); setEdits(null)
    setReprovisionConfirm(false); setSaveState('idle')
    setProjectAccess(null)
  }

  // A project-access change is its own write, outside Save. A refusal is shown on the dialog's
  // status line, and the list is re-read either way so it shows what the server holds.
  async function changeProjectAccess(write: (url: string) => Promise<unknown>) {
    if (!agent) return
    const url = `/api/agents/${encodeURIComponent(agent)}/project-access`
    try {
      await write(url)
    } catch (e) {
      report('error', `Project access not changed: ${errorMessage(e)}`, 5000)
    }
    try {
      const answer = await apiGet<{ projects: string[] }>(url)
      setProjectAccess(answer.projects)
    } catch { /* the list shown stays as it was */ }
  }

  const addProjectAccess = (project: string) => changeProjectAccess(url => apiSend(url, 'POST', { project }))
  const removeProjectAccess = (project: string) => changeProjectAccess(url => apiSend(`${url}/${encodeURIComponent(project)}`, 'DELETE'))

  function save(andReprovision: boolean) {
    if (!agent || !edits) return
    // The body is built in agentConfigSave.ts (#460 AC-D2: the same body the unsectioned dialog sent).
    const body = configSaveBody(edits, data?.harnessExtra, data?.chatEnabled)
    if (!body.ok) { setSaveMsg(body.error); setSaveState('error'); return }
    const projects = body.body.projects as string[]
    const users = idList(edits.telegramUsers)
    const groups = idList(edits.telegramGroups)
    const prevUsers = data?.telegramUsers ?? []
    const prevGroups = data?.telegramGroups ?? []
    timers.cancel('config-save')
    setSaveState('saving')
    const agentUrl = `/api/agents/${encodeURIComponent(agent)}`
    void (async () => {
      try {
        await apiSend(`${agentUrl}/config`, 'PUT', body.body)
      } catch (e) { report('error', errorMessage(e), 5000); return }
      // The config is stored from here on. A later step that is refused says so, and is never
      // reported as "Saved" nor as a save that did not happen.
      setData(prev => prev ? { ...prev, projects, harnessExtra: edits.harnessExtra === '' ? null : edits.harnessExtra, chatEnabled: edits.chatEnabled } : prev)
      // Telegram users and groups go through their own endpoints (live, no reprovision needed).
      const allowlist = firstFailure(await Promise.allSettled([
        ...users.filter(id => !prevUsers.includes(id)).map(id => apiSend(`${agentUrl}/telegram-users`, 'POST', { userId: id })),
        ...prevUsers.filter(id => !users.includes(id)).map(id => apiSend(`${agentUrl}/telegram-users/${id}`, 'DELETE')),
        ...groups.filter(id => !prevGroups.includes(id)).map(id => apiSend(`${agentUrl}/telegram-groups`, 'POST', { groupId: id })),
        ...prevGroups.filter(id => !groups.includes(id)).map(id => apiSend(`${agentUrl}/telegram-groups/${id}`, 'DELETE')),
      ]))
      if (allowlist) {
        report('error', `Saved, but ${allowlist.failed} Telegram allowlist change${allowlist.failed === 1 ? '' : 's'} failed: ${allowlist.message}`, 5000)
        return
      }
      if (andReprovision) {
        try {
          await apiSend(`${agentUrl}/reprovision`, 'POST')
        } catch (e) { report('error', `Saved, but reprovision failed: ${errorMessage(e)}`, 5000); return }
      }
      report('success', andReprovision ? 'Saved & reprovisioning…' : 'Saved', 4000)
    })()
  }

  return {
    agent, data, edits, saveState, saveMsg, loading, reprovisionConfirm, projectAccess, projectAccessLoading,
    open, close,
    change: patch => setEdits(prev => prev ? { ...prev, ...patch } : prev),
    save,
    toggleReprovisionConfirm: () => setReprovisionConfirm(c => !c),
    addProjectAccess, removeProjectAccess,
    toggleProjectAccessWildcard: enable => enable ? addProjectAccess('*') : removeProjectAccess('*'),
  }
}
