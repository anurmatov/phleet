import type { AgentConfig, CreateForm } from './types'
import { PROVIDER_DEFAULT_MODEL } from './constants.ts'
import { parseWarmupTimeoutSeconds, warmupTimeoutEditValue } from './warmupTimeout.ts'
import { contextWindowEditValue } from './contextWindow.ts'
import { isLocalHarness, localModelEditValues, localModelSaveBody, type LocalModelEdits } from './localModel.ts'

// The New Agent dialog's form, what "Copy from existing agent" puts in it, and the
// POST /api/agents body it sends. Moved out of App.tsx so a test can hold each against the
// orchestrator's own shapes. Kept free of JSX and `import.meta.env` so `node --test` can run it.

export const DEFAULT_CREATE_FORM: CreateForm = {
  name: '', displayName: '', role: '', model: PROVIDER_DEFAULT_MODEL['claude'],
  containerName: '', memoryLimitMb: '4096', isEnabled: true, image: '',
  permissionMode: 'acceptEdits', maxTurns: '50', workDir: '/workspace',
  proactiveIntervalMinutes: '0',
  groupListenMode: 'mention', groupDebounceSeconds: '15', warmupTimeoutSeconds: '60', shortName: '',
  effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
  showStats: true, prefixMessages: false, formattingMode: 0, suppressToolMessages: false, telegramSendOnly: false, provider: 'claude',
  codexSandboxMode: '',
  localMode: false, localBaseUrl: '', localServer: 'ollama', contextWindow: '',
  tools: '', projects: '', networks: '', envRefs: '',
  mcpEndpoints: [], telegramUsers: '', telegramGroups: '', instructions: [],
}

/**
 * The form after "Copy from existing agent". `cfg` is `GET /api/agents/{name}/config`, which
 * carries no role; `role` comes from the agent list (`GET /api/agents`), and a source with none
 * leaves the field as it was. Name, short name and display name are never copied.
 *
 * The local server URL, the mode it implies and the context window are copied too: without them a
 * copy of a local-model agent was created as a cloud agent holding a local model tag.
 */
export function createFormFromConfig(prev: CreateForm, cfg: AgentConfig, role: string | null | undefined): CreateForm {
  return {
    ...prev,
    role: role || prev.role,
    model: cfg.model,
    memoryLimitMb: String(cfg.memoryLimitMb),
    image: cfg.image ?? '',
    permissionMode: cfg.permissionMode,
    maxTurns: String(cfg.maxTurns),
    workDir: cfg.workDir,
    proactiveIntervalMinutes: String(cfg.proactiveIntervalMinutes),
    groupListenMode: cfg.groupListenMode,
    groupDebounceSeconds: String(cfg.groupDebounceSeconds),
    warmupTimeoutSeconds: warmupTimeoutEditValue(cfg.warmupTimeoutSeconds),
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
    tools: cfg.tools.filter(t => t.isEnabled).map(t => t.toolName).join('\n'),
    projects: cfg.projects.join('\n'),
    networks: cfg.networks.join('\n'),
    envRefs: cfg.envRefs.join('\n'),
    mcpEndpoints: cfg.mcpEndpoints,
    telegramUsers: cfg.telegramUsers.join('\n'),
    telegramGroups: cfg.telegramGroups.join('\n'),
    ...localModelEditValues(cfg),
    contextWindow: contextWindowEditValue(cfg.contextWindow),
  }
}

const lines = (raw: string): string[] => raw.split('\n').map(s => s.trim()).filter(Boolean)
const ids = (raw: string): number[] => raw.split('\n').map(s => parseInt(s.trim())).filter(n => !isNaN(n))

/**
 * The form as the local-model rules read it. The create form lists Env Refs one per line; those
 * rules (shared with the config dialog) read them comma-separated.
 */
export function createFormLocalEdits(form: CreateForm): LocalModelEdits {
  return {
    provider: form.provider || 'claude',
    model: form.model,
    effort: form.effort,
    localMode: form.localMode,
    localServer: form.localServer,
    localBaseUrl: form.localBaseUrl,
    contextWindow: form.contextWindow,
    envRefs: lines(form.envRefs).join(','),
  }
}

export type CreateAgentBody =
  | { ok: true; body: Record<string, unknown> }
  | { ok: false; error: string }

/**
 * The POST /api/agents body for `form`, or what is missing from it. `provision` asks the
 * orchestrator to start the container as part of the create (a template-based create does).
 *
 * `localBaseUrl` and `contextWindow` are sent only for a local-model agent, so a cloud create
 * sends exactly the body it always did.
 */
export function createAgentBody(form: CreateForm, provision: boolean): CreateAgentBody {
  const name = form.name.trim()
  if (!name) return { ok: false, error: 'Name is required' }
  const provider = form.provider || 'claude'
  const local = form.localMode && isLocalHarness(provider)
  // The model, with the local rules when they apply: a server URL and a model tag are required,
  // and a Codex tag is stored with its `<server>/` prefix.
  const localModel = localModelSaveBody(createFormLocalEdits(form))
  if (!localModel.ok) return { ok: false, error: localModel.error }
  if (!form.role.trim()) return { ok: false, error: 'Role is required' }
  const warmupTimeout = parseWarmupTimeoutSeconds(form.warmupTimeoutSeconds)
  if (!warmupTimeout.ok) return { ok: false, error: warmupTimeout.error }

  const body: Record<string, unknown> = {
    name,
    displayName: form.displayName.trim() || name,
    role: form.role.trim(),
    model: localModel.body.model,
    containerName: form.containerName.trim() || `fleet-${name}`,
    memoryLimitMb: parseInt(form.memoryLimitMb) || 4096,
    isEnabled: form.isEnabled,
    image: form.image.trim() || null,
    permissionMode: form.permissionMode || 'acceptEdits',
    maxTurns: parseInt(form.maxTurns) || 50,
    workDir: form.workDir || '/workspace',
    proactiveIntervalMinutes: parseInt(form.proactiveIntervalMinutes) || 0,
    groupListenMode: form.groupListenMode || 'mention',
    groupDebounceSeconds: parseInt(form.groupDebounceSeconds) || 15,
    warmupTimeoutSeconds: warmupTimeout.value,
    shortName: form.shortName,
    showStats: form.showStats,
    prefixMessages: form.prefixMessages,
    formattingMode: form.formattingMode,
    suppressToolMessages: form.suppressToolMessages,
    telegramSendOnly: form.telegramSendOnly,
    effort: form.effort.trim() || null,
    jsonSchema: form.jsonSchema.trim() || null,
    agentsJson: form.agentsJson.trim() || null,
    autoMemoryEnabled: form.autoMemoryEnabled,
    provider,
    codexSandboxMode: form.codexSandboxMode.trim() || null,
    tools: lines(form.tools),
    projects: lines(form.projects),
    networks: lines(form.networks),
    envRefs: lines(form.envRefs),
    mcpEndpoints: form.mcpEndpoints,
    telegramUsers: ids(form.telegramUsers),
    telegramGroups: ids(form.telegramGroups),
    provision,
  }
  if (local) {
    // '' is the legacy Codex case (the server is reached through an Env Ref); the API reads it,
    // and a context window of 0, as not set.
    if (localModel.body.localBaseUrl !== '') body.localBaseUrl = localModel.body.localBaseUrl
    if (localModel.body.contextWindow !== 0) body.contextWindow = localModel.body.contextWindow
  }
  return { ok: true, body }
}
