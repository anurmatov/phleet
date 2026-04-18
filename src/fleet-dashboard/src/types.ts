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
  shortName: string
  ttsServiceUrl: string
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  showStats: boolean
  prefixMessages: boolean
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
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  suppressToolMessages: boolean
  ttsServiceUrl: string
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  hostPort: number | null
  telegramSendOnly: boolean
  provider: string
  codexSandboxMode: string | null
  tools: { toolName: string; isEnabled: boolean }[]
  projects: string[]
  mcpEndpoints: McpEndpointEntry[]
  networks: string[]
  envRefs: string[]
  telegramUsers: number[]
  telegramGroups: number[]
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
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  suppressToolMessages: boolean
  ttsServiceUrl: string
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  hostPort: string
  telegramSendOnly: boolean
  provider?: string
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

export interface AgentTemplateSummary {
  name: string
  displayName: string
  description: string
  defaultModel: string
  toolCount: number
  mcpCount: number
}

export interface InstructionSummary {
  name: string
  currentVersion: number
  isActive: boolean
  totalVersions: number
  agents: string[]
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
export type ActiveView = 'agents' | 'workflows' | 'instructions' | 'project-contexts' | 'wf-definitions' | 'alerts' | 'schedules' | 'namespaces' | 'repositories'

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
