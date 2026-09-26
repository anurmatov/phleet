// Shared type definitions for the Fleet Dashboard

export interface QueuedMessageInfo {
  preview: string
  source: string
  queuedAt: string
}

export interface BackgroundTaskSummary {
  taskId: string
  taskType: string
  description: string
  elapsedSeconds: number
  summary: string | null
}

export interface AgentState {
  agentName: string
  displayName: string | null
  shortName: string | null
  model: string | null
  role: string | null
  currentTask: string | null
  currentTaskId: string | null
  reportedStatus: string
  effectiveStatus: string
  lastSeen: string
  version: string | null
  queuedCount: number
  queuedMessages: QueuedMessageInfo[] | null
  backgroundTasks: BackgroundTaskSummary[] | null
  containerName: string | null
  containerStartedAt: string | null
  hostPort: number | null
}

export interface TaskRecord {
  agentName: string
  taskText: string
  startedAt: string
  completedAt: string
  durationSeconds: number
}

export interface WorkflowSummary {
  workflowId: string
  runId: string
  workflowType: string
  namespace: string
  taskQueue: string | null
  status: string
  startTime: string
  closeTime: string | null
  issueNumber: number | null
  prNumber: number | null
  repo: string | null
  docPrs: string | null
  phase: string | null
}

export interface WorkflowEvent {
  eventId: number
  eventType: string
  timestamp: string
  activityType: string | null
  agent: string | null
  inputSummary: string | null
  outputSummary: string | null
  failureMessage: string | null
  signalName: string | null
}

export interface SignalButton {
  label: string
  payload: string
  requiresComment: boolean
}

export interface SignalDef {
  name: string
  label: string
  buttons: SignalButton[]
  validPhases: string[] | null
}

export interface SignalModalState {
  wf: WorkflowSummary
  signalName: string
  button: SignalButton
  comment: string
}

export interface Alert {
  id: string
  type: 'agent-dead' | 'agent-stale' | 'task-stuck' | 'workflow-failed' | 'info'
  message: string
  timestamp: string
  dismissed: boolean
  showToast: boolean
  workflowId?: string
  workflowNamespace?: string
  agentName?: string
}

export interface McpEndpointEntry {
  mcpName: string
  url: string
  transportType: string
}

export interface CreateForm {
  name: string
  displayName: string
  role: string
  model: string
  containerName: string
  memoryLimitMb: string
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: string
  workDir: string
  proactiveIntervalMinutes: string
  groupListenMode: string
  groupDebounceSeconds: string
  warmupTimeoutSeconds: string
  shortName: string
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  telegramSendOnly: boolean
  provider: string
  codexSandboxMode: string
  tools: string
  projects: string
  networks: string
  envRefs: string
  mcpEndpoints: McpEndpointEntry[]
  telegramUsers: string
  telegramGroups: string
  instructions: { name: string; loadOrder: number }[]
}

export interface AgentConfig {
  agentName: string
  model: string
  memoryLimitMb: number
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: number
  workDir: string
  proactiveIntervalMinutes: number
  groupListenMode: string
  groupDebounceSeconds: number
  warmupTimeoutSeconds: number
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  hostPort: number | null
  telegramSendOnly: boolean
  provider: string
  codexSandboxMode: string | null
  outputStyle: string | null
  /** Origin of a local Anthropic-compatible server, or null (Anthropic). Claude agents only. */
  anthropicBaseUrl: string | null
  /** The local server's context size in tokens, or null. Used only in local mode (#367). */
  contextWindow?: number | null
  tools: { toolName: string; isEnabled: boolean }[]
  projects: string[]
  mcpEndpoints: McpEndpointEntry[]
  networks: string[]
  envRefs: string[]
  telegramUsers: number[]
  telegramGroups: number[]
  canReceiveChatRequests: boolean
  requestReceivedMessage: string | null
  mountDockerSock: boolean
  instructions: { name: string; loadOrder: number }[]
}

export interface ConfigEdits {
  model: string
  memoryLimitMb: string
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: string
  workDir: string
  proactiveIntervalMinutes: string
  groupListenMode: string
  groupDebounceSeconds: string
  warmupTimeoutSeconds: string
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  hostPort: string
  telegramSendOnly: boolean
  provider?: string
  codexSandboxMode: string
  tools: string
  /** Assigned project names in stored order, edited only through the project table. */
  projects: string[]
  networks: string
  envRefs: string
  mcpEndpoints: McpEndpointEntry[]
  telegramUsers: string
  telegramGroups: string
  canReceiveChatRequests: boolean
  requestReceivedMessage: string
  mountDockerSock: boolean
  /** Name of an output_styles row, or '' for none — the sentinel the API clears on. */
  outputStyle: string
  /** Local Anthropic-compatible server origin, or '' for Anthropic — the sentinel the API clears on. */
  anthropicBaseUrl: string
  /** Context window in tokens, or '' for not set (sent as 0, which the API clears on). */
  contextWindow: string
  instructions: { name: string; loadOrder: number }[]
}

export interface AgentTemplateSummary {
  name: string
  displayName: string
  description: string
  defaultModel: string
  toolCount: number
  mcpCount: number
}

export interface OutputStyleSummary {
  name: string
  description: string | null
}

/** A style row with its full body and the agents assigned to it — what GET /api/output-styles returns. */
export interface OutputStyleDetail extends OutputStyleSummary {
  body: string
  agents: string[]
}

export interface InstructionSummary {
  name: string
  currentVersion: number
  isActive: boolean
  totalVersions: number
  agents: string[]
  /** UTF-8 bytes of the current version. Absent on an older orchestrator. */
  currentBytes?: number | null
}

export interface InstructionVersion {
  versionNumber: number
  content: string
  createdAt: string
  createdBy: string
  reason: string
}

export interface InstructionDetail {
  name: string
  currentVersion: number
  versions: InstructionVersion[]
}

export type WsStatus = 'connecting' | 'connected' | 'disconnected'
export type RestartState = 'idle' | 'confirming' | 'restarting' | 'success' | 'error'
export type ReprovisionState = 'idle' | 'confirming' | 'provisioning' | 'success' | 'error'
export type StopStartState = 'idle' | 'confirming' | 'pending' | 'success' | 'error'
export type WfActionState = 'idle' | 'confirming-cancel' | 'confirming-restart' | 'confirming-terminate' | 'pending' | 'success' | 'error'
export type CancelState = 'idle' | 'confirming' | 'cancelling' | 'success' | 'error'
export type ConfigSaveState = 'idle' | 'saving' | 'success' | 'error'
export type ActiveView = 'agents' | 'workflows' | 'instructions' | 'project-contexts' | 'output-styles' | 'wf-definitions' | 'alerts' | 'schedules' | 'namespaces' | 'repositories' | 'credentials' | 'memory'

// ── Memory types ──────────────────────────────────────────────────────────────

export interface MemoryListItem {
  id: string
  title: string
  project: string
  type: string
  tags: string[]
  updated_at: string
}

export interface MemoryDoc {
  id: string
  title: string
  type: string
  agent: string
  project: string
  tags: string[]
  source: string
  created_at: string
  updated_at: string
  content: string
  /** #346 — absent on an older fleet-memory. */
  size_guidance?: MemoryEmbeddingSize
}

/** #346 — `GET /internal/memory/{id}` size guidance; `limit_bytes` is null when the guidance is off. */
export interface MemoryEmbeddingSize {
  unit: 'utf8Bytes'
  bytes: number
  limit_bytes: number | null
  limit_key: string
}

/** #346 — the `size` report on a memory PUT (snake_case, like the rest of the fleet-memory API). */
export interface MemorySizeReport {
  kind: 'memory'
  unit: 'utf8Bytes'
  previous_bytes: number | null
  bytes: number
  limit_bytes: number | null
  limit_key: string
  status: 'under' | 'crossed' | 'stillOver' | 'disabled'
  warning: string | null
}

/** #346 — the `size` report on an instruction or project-context write. */
export interface PromptSizeReport {
  kind: 'instruction' | 'projectContext'
  unit: 'utf8Bytes'
  previousBytes: number | null
  bytes: number
  limitBytes: number | null
  limitKey: string
  status: 'under' | 'crossed' | 'stillOver' | 'disabled'
  warning: string | null
}

/** #346 — `GET /api/prompt-size-policy`; `limitBytes` is null when that check is disabled. */
export interface PromptSizeLimits {
  unit: 'utf8Bytes'
  instruction: { limitBytes: number | null; limitKey: string }
  projectContext: { limitBytes: number | null; limitKey: string }
}

export interface MemorySearchResult {
  id: string
  title: string
  project: string
  type: string
  tags: string[]
  snippet: string
  updated_at: string
}

export interface AgentReadStatsDto {
  count: number
  lastReadAt: string
}

export interface MemoryReadStatsDto {
  memoryId: string
  total: number
  byAgent: Record<string, AgentReadStatsDto>
  lastReadAt: string
}

export interface MemoryStatsResponse {
  since: string
  entries: MemoryReadStatsDto[]
}

export interface ScheduleSummary {
  scheduleId: string
  namespace: string
  workflowType: string | null
  cronExpression: string | null
  paused: boolean
  memo: string | null
}

export interface ScheduleDetail extends ScheduleSummary {
  nextRunTime: string | null
  lastRunTime: string | null
  lastRunWorkflowId: string | null
  input: unknown
}
export type ReprovisionAllState = 'idle' | 'confirming' | 'running' | 'success' | 'error'

export interface WorkflowDefinitionSummary {
  name: string
  namespace: string
  taskQueue: string
  version: number
  isActive: boolean
  description?: string
  updatedAt: string
}

export interface WorkflowDefinitionVersion {
  version: number
  definition: string
  reason?: string
  createdAt: string
  createdBy?: string
}

export interface WorkflowDefinitionDetail extends WorkflowDefinitionSummary {
  definition: string
  versions: WorkflowDefinitionVersion[]
}


export interface ProjectContextSummary {
  name: string
  currentVersion: number
  isActive: boolean
  totalVersions: number
  agents: string[]
  /** UTF-8 bytes of the canonical context (the current version). Absent on an older orchestrator. */
  currentBytes?: number | null
}

export interface ProjectContextVersion {
  versionNumber: number
  content: string
  createdAt: string
  createdBy: string
  reason: string
}

export interface ProjectContextDetail {
  name: string
  currentVersion: number
  versions: ProjectContextVersion[]
}

export interface WorkflowTypeInfo {
  name: string
  description: string
  namespace: string
  taskQueue: string
  inputSchema: string | null
}
