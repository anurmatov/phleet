import type { ConfigEdits } from './types'
import { projectPayload } from './projectAssignments.ts'
import { parseWarmupTimeoutSeconds } from './warmupTimeout.ts'
import { localModelSaveBody, parseEnvRefs } from './localModel.ts'

// The PUT /api/agents/{name}/config body the agent config dialog sends. Moved out of App.tsx
// unchanged so a test can compare it with a body recorded before the dialog was sectioned (#460
// AC-D2). Kept free of JSX and `import.meta.env` so `node --test` can run it.

export type ConfigSaveBody =
  | { ok: true; body: Record<string, unknown> }
  | { ok: false; error: string }

/**
 * The request body for `edits`. `savedHarnessExtra` is the stored `harnessExtra` the dialog loaded:
 * the field is sent only when it was edited, so every other save sends exactly what it sent before
 * #460, and an older orchestrator never sees the key.
 */
export function configSaveBody(edits: ConfigEdits, savedHarnessExtra: string | null | undefined): ConfigSaveBody {
  const provider = (edits.provider ?? 'claude').trim()
  // Model, local server URL (sent as `localBaseUrl` only) and context window, with the empty-URL rule;
  // Pi also sends its image input, which every other provider omits (kept).
  const localModel = localModelSaveBody({ ...edits, provider })
  if (!localModel.ok) return { ok: false, error: localModel.error }
  const memoryLimitMb = parseInt(edits.memoryLimitMb, 10)
  if (isNaN(memoryLimitMb) || memoryLimitMb < 128) return { ok: false, error: 'Memory must be ≥ 128 MB' }
  const maxTurns = parseInt(edits.maxTurns, 10)
  const warmupTimeout = parseWarmupTimeoutSeconds(edits.warmupTimeoutSeconds)
  if (!warmupTimeout.ok) return { ok: false, error: warmupTimeout.error }
  const tools = edits.tools.split(',').map(t => t.trim()).filter(Boolean)
  const { projects } = projectPayload(edits)
  const networks = edits.networks.split(',').map(n => n.trim()).filter(Boolean)
  const envRefs = parseEnvRefs(edits.envRefs)
  const body: Record<string, unknown> = {
    ...localModel.body,
    memoryLimitMb,
    isEnabled: edits.isEnabled,
    image: edits.image,
    permissionMode: edits.permissionMode,
    maxTurns: isNaN(maxTurns) ? undefined : maxTurns,
    workDir: edits.workDir,
    proactiveIntervalMinutes: parseInt(edits.proactiveIntervalMinutes, 10) || 0,
    groupListenMode: edits.groupListenMode,
    groupDebounceSeconds: parseInt(edits.groupDebounceSeconds, 10) || 15,
    warmupTimeoutSeconds: warmupTimeout.value,
    shortName: edits.shortName,
    displayName: edits.displayName,
    showStats: edits.showStats,
    prefixMessages: edits.prefixMessages,
    formattingMode: edits.formattingMode,
    suppressToolMessages: edits.suppressToolMessages,
    telegramSendOnly: edits.telegramSendOnly,
    provider: edits.provider,
    effort: edits.effort,
    jsonSchema: edits.jsonSchema,
    agentsJson: edits.agentsJson,
    autoMemoryEnabled: edits.autoMemoryEnabled,
    journalEnabled: edits.journalEnabled,
    journalCrossChatEnabled: edits.journalCrossChatEnabled,
    codexSandboxMode: edits.codexSandboxMode,
    hostPort: edits.hostPort ? parseInt(edits.hostPort, 10) : null,
    canReceiveChatRequests: edits.canReceiveChatRequests,
    requestReceivedMessage: edits.requestReceivedMessage || undefined,
    mountDockerSock: edits.mountDockerSock,
    outputStyle: edits.outputStyle,
    tools, projects, mcpEndpoints: edits.mcpEndpoints, networks, envRefs,
    instructions: edits.instructions.map(i => ({ instructionName: i.name, loadOrder: i.loadOrder })),
  }
  // #460 Part 3b: "" clears the stored value; sent only when the dialog's text differs from it.
  if (edits.harnessExtra !== (savedHarnessExtra ?? '')) body.harnessExtra = edits.harnessExtra
  return { ok: true, body }
}
